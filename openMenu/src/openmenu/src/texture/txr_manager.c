/*
 * File: txr_manager.c
 * Project: texture
 * File Created: Friday, 21st May 2021 2:30:13 pm
 * Author: Hayden Kowalchuk
 * -----
 * Copyright (c) 2021 Hayden Kowalchuk, Hayden Kowalchuk
 * License: BSD 3-clause "New" or "Revised" License,
 * http://www.opensource.org/licenses/BSD-3-Clause
 */

#include <malloc.h>
#include <stdio.h>
#include <string.h>

#include <dc/pvr.h>
#include <kos/cond.h>
#include <kos/mutex.h>
#include <kos/thread.h>

#include <backend/dat_format.h>
#include <texture/serial_sanitize.h>
#include "block_pool.h"
#include "lru.h"
#include "ui/draw_kos.h"
#include "ui/draw_prototypes.h"

#include "txr_manager.h"

/* CFG for small pvr pool (128x128 16bit, 16 spaces) */
#define SM_SLOT_NUM  (16)
#define SM_SLOT_SIZE (128 * 128 * 2)
#define SM_POOL_SIZE (SM_SLOT_NUM * SM_SLOT_SIZE * sizeof(char))

/* CFG for large pvr pool (256x256 16bit, 8 spaces) */
#define LG_SLOT_NUM  (8)
#define LG_SLOT_SIZE (256 * 256 * 2)
#define LG_POOL_SIZE (LG_SLOT_NUM * LG_SLOT_SIZE * sizeof(char))

typedef struct dat_system {
    cache_instance cache;
    block_pool pool;
    struct dat_file addon;
    struct dat_file primary;
} dat_system;

static dat_system icon_system;
static dat_system box_system;

/* Folder artwork entries, box format, shares the large pool and cache */
static struct dat_file folder_dat;

static int defer_loads;

/* Background box art loading.
 *
 * A thread reads one DAT chunk at a time into its own buffer. The render loop
 * then does the VRAM copy and the cache insert, so the thread never touches
 * either. After a read the thread waits until that buffer has been consumed.
 *
 * "wanted" is ordered by priority and replaced whenever the selection moves,
 * which is what drops the requests that have not started yet. */

/* The selection plus four grid neighbours, must stay below LG_SLOT_NUM */
#define PRELOAD_MAX (5)
#define ART_KEY_LEN (16)

typedef struct preload_req {
    char key[ART_KEY_LEN];
    const dat_file* source;
    int done; /* Cached, or already taken by the thread */
} preload_req;

/* The BOX DAT handles are shared by the thread and the render loop. A handle
 * for each would not fit: KOS allows 8 open files on /cd. */
static mutex_t box_read_mutex = MUTEX_INITIALIZER;

/* Guards wanted[].done, wanted_num, loading_key, result_key and async_quit */
static mutex_t async_mutex = MUTEX_INITIALIZER;
static condvar_t async_cond = COND_INITIALIZER;

static kthread_t* async_thread;
static void* async_buffer;
static int async_quit;
static int async_stopped;

static preload_req wanted[PRELOAD_MAX];
static int wanted_num;
static char loading_key[ART_KEY_LEN]; /* Chunk being read, empty when idle */
static char result_key[ART_KEY_LEN];  /* Chunk waiting in async_buffer, empty when none */

/* The list being built between txr_preload_begin and txr_preload_end */
static preload_req next_wanted[PRELOAD_MAX];
static int next_wanted_num;

unsigned int
block_pool_add_cb(const char* key, void* user) {
    /* unused here but could be good info to know */
    (void)key;

    block_pool* pool = (block_pool*)user;
    unsigned int ret;
    char* ptr;
    pool_get_next_free(pool, &ret, (void**)&ptr);
    return ret;
}

unsigned int
block_pool_del_cb(const char* key, void* value, void* user) {
    /* unused here but could be good info to know */
    (void)key;

    block_pool* pool = (block_pool*)user;
    unsigned int slot_num = *(unsigned int*)value;
    pool_dealloc_slot(pool, slot_num);
    return 0;
}

int
txr_load_DATs(void) {
    serial_sanitizer_init(); /*@Todo: Move this */

    DAT_init(&icon_system.addon);
    DAT_init(&icon_system.primary);
    DAT_init(&box_system.addon);
    DAT_init(&box_system.primary);
    DAT_init(&folder_dat);

    DAT_load_parse(&icon_system.primary, "ICON.DAT");
    DAT_load_parse(&box_system.primary, "BOX.DAT");
    DAT_load_parse(&icon_system.addon, "ICON_EX.DAT");
    DAT_load_parse(&box_system.addon, "BOX_EX.DAT");
    DAT_load_parse(&folder_dat, "FOLDRART.DAT");

    return 0;
}

int
txr_create_small_pool(void) {
    void* buffer = pvr_mem_malloc(SM_POOL_SIZE);
    pool_create(&icon_system.pool, buffer, SM_POOL_SIZE, SM_SLOT_NUM);
    icon_system.cache.cache = NULL;
    cache_set_size(&icon_system.cache, SM_SLOT_NUM);
    cache_callback_userdata(&icon_system.cache, &icon_system.pool);
    cache_callback_add(&icon_system.cache, block_pool_add_cb);
    cache_callback_del(&icon_system.cache, block_pool_del_cb);

    return 0;
}

/* pool_destroy_user(&pvr_small, pvr_mem_free); */

int
txr_create_large_pool(void) {
    void* buffer = pvr_mem_malloc(LG_POOL_SIZE);
    pool_create(&box_system.pool, buffer, LG_POOL_SIZE, LG_SLOT_NUM);
    box_system.cache.cache = NULL;
    cache_set_size(&box_system.cache, LG_SLOT_NUM);
    cache_callback_userdata(&box_system.cache, &box_system.pool);
    cache_callback_add(&box_system.cache, block_pool_add_cb);
    cache_callback_del(&box_system.cache, block_pool_del_cb);
    return 0;
}

void
txr_empty_small_pool(void) {
    empty_cache(&icon_system.cache);
    pool_dealloc_all(&icon_system.pool);
}

/* Empties the queue and waits out any read in progress */
static void
txr_async_flush(void) {
    if (!async_thread) {
        return;
    }
    mutex_lock(&async_mutex);
    wanted_num = 0;
    while (loading_key[0]) {
        cond_wait(&async_cond, &async_mutex);
    }
    result_key[0] = '\0';
    cond_broadcast(&async_cond);
    mutex_unlock(&async_mutex);
}

void
txr_empty_large_pool(void) {
    txr_async_flush();
    empty_cache(&box_system.cache);
    pool_dealloc_all(&box_system.pool);
}

void
txr_defer_loads(int defer) {
    defer_loads = defer;
}

static int
txr_get_from_dat_set(const char* id, struct image* img, dat_system* system) {
    void* txr_ptr;
    int slot_num;
    const char* id_santized = serial_santize_art(id);

    /* Initially check addon then fall back to regular */
    uint32_t temp_offset = DAT_get_offset_by_ID(&system->addon, id_santized);
    const dat_file* dat_source = &system->addon;
    if (!temp_offset) {
        temp_offset = DAT_get_offset_by_ID(&system->primary, id_santized);
        dat_source = &system->primary;
        if (!temp_offset) {
            dat_source = NULL;
        }
    }

    /* check if exists in DAT and if not, return missing image */
    if (!dat_source) {
        draw_load_missing_icon(img);
        return 0;
    }
    slot_num = find_in_cache(&system->cache, id_santized);
    if (slot_num == -1) {
        if (defer_loads) {
            draw_load_missing_icon(img);
            return TXR_DEFERRED;
        }
        add_to_cache(&system->cache, id_santized, 0);
        slot_num = find_in_cache(&system->cache, id_santized);
        txr_ptr = pool_get_slot_addr(&system->pool, slot_num);

        if (system == &box_system) {
            mutex_lock(&box_read_mutex);
        }
        draw_load_texture_from_DAT_to_buffer(dat_source, id_santized, img, txr_ptr);
        if (system == &box_system) {
            mutex_unlock(&box_read_mutex);
        }
        pool_set_slot_format(&system->pool, slot_num, img->width, img->height, img->format);
    } else {
        const slot_format* fmt = pool_get_slot_format(&system->pool, slot_num);
        img->width = fmt->width;
        img->height = fmt->height;
        img->format = fmt->format;
        img->texture = pool_get_slot_addr(&system->pool, slot_num);
    }
    return 0;
}

/*
called with "T1121.pvr" and a pointer to pointer to vram
returns pointer to use for texture upload/reference
 */
int
txr_get_small(const char* id, struct image* img) {
    return txr_get_from_dat_set(id, img, &icon_system);
}

int
txr_get_large(const char* id, struct image* img) {
    return txr_get_from_dat_set(id, img, &box_system);
}

/* Folder artwork lookup. Searches only FOLDRART.DAT but shares the large
 * pool and cache. Keys are hashed folder paths so no serial sanitizing. */
int
txr_get_folder(const char* id, struct image* img) {
    void* txr_ptr;
    int slot_num;

    uint32_t temp_offset = DAT_get_offset_by_ID(&folder_dat, id);
    if (!temp_offset) {
        draw_load_missing_icon(img);
        return 0;
    }
    slot_num = find_in_cache(&box_system.cache, id);
    if (slot_num == -1) {
        if (defer_loads) {
            draw_load_missing_icon(img);
            return TXR_DEFERRED;
        }
        add_to_cache(&box_system.cache, id, 0);
        slot_num = find_in_cache(&box_system.cache, id);
        txr_ptr = pool_get_slot_addr(&box_system.pool, slot_num);

        draw_load_texture_from_DAT_to_buffer(&folder_dat, id, img, txr_ptr);
        pool_set_slot_format(&box_system.pool, slot_num, img->width, img->height, img->format);
    } else {
        const slot_format* fmt = pool_get_slot_format(&box_system.pool, slot_num);
        img->width = fmt->width;
        img->height = fmt->height;
        img->format = fmt->format;
        img->texture = pool_get_slot_addr(&box_system.pool, slot_num);
    }
    return 0;
}

static int
async_next_request(void) {
    if (result_key[0]) {
        return -1;
    }
    for (int i = 0; i < wanted_num; i++) {
        if (!wanted[i].done) {
            return i;
        }
    }
    return -1;
}

static void*
async_main(void* arg) {
    char key[ART_KEY_LEN];
    const dat_file* source;

    (void)arg;

    mutex_lock(&async_mutex);
    while (!async_quit) {
        int idx = async_next_request();
        if (idx < 0) {
            cond_wait(&async_cond, &async_mutex);
            continue;
        }

        wanted[idx].done = 1;
        strcpy(key, wanted[idx].key);
        strcpy(loading_key, key);
        source = wanted[idx].source;
        mutex_unlock(&async_mutex);

        mutex_lock(&box_read_mutex);
        int found = DAT_read_file_by_ID(source, key, async_buffer);
        mutex_unlock(&box_read_mutex);

        mutex_lock(&async_mutex);
        loading_key[0] = '\0';
        if (found) {
            strcpy(result_key, key);
        }
        cond_broadcast(&async_cond);
    }
    mutex_unlock(&async_mutex);
    return NULL;
}

/* The thread runs at the default priority on purpose. The render loop only
 * gives up the CPU while blocked, a lower priority would rarely get to run. */
static void
async_start(void) {
    if (async_thread || async_stopped) {
        return;
    }

    uint32_t chunk_size = box_system.primary.chunk_size;
    if (box_system.addon.chunk_size > chunk_size) {
        chunk_size = box_system.addon.chunk_size;
    }

    async_buffer = chunk_size ? memalign(32, chunk_size) : NULL;
    if (async_buffer) {
        async_thread = thd_create(false, async_main, NULL);
    }
    if (!async_thread) {
        async_stopped = 1;
    }
}

void
txr_preload_begin(void) {
    next_wanted_num = 0;
}

void
txr_preload_add(const char* id) {
    if (next_wanted_num >= PRELOAD_MAX || !id || !id[0]) {
        return;
    }

    const char* key = serial_santize_art(id);
    if (strlen(key) >= ART_KEY_LEN) {
        return;
    }

    /* Same lookup order as txr_get_from_dat_set */
    const dat_file* source = &box_system.addon;
    if (!DAT_get_offset_by_ID(source, key)) {
        source = &box_system.primary;
        if (!DAT_get_offset_by_ID(source, key)) {
            return;
        }
    }

    for (int i = 0; i < next_wanted_num; i++) {
        if (!strcmp(next_wanted[i].key, key)) {
            return;
        }
    }

    preload_req* req = &next_wanted[next_wanted_num++];
    strcpy(req->key, key);
    req->source = source;
    req->done = 0;
}

void
txr_preload_end(void) {
    async_start();
    if (!async_thread) {
        return;
    }

    mutex_lock(&async_mutex);
    for (int i = 0; i < next_wanted_num; i++) {
        preload_req* req = &next_wanted[i];
        req->done = find_in_cache(&box_system.cache, req->key) != -1 || !strcmp(req->key, loading_key)
                    || !strcmp(req->key, result_key);
        wanted[i] = *req;
    }
    wanted_num = next_wanted_num;
    cond_broadcast(&async_cond);
    mutex_unlock(&async_mutex);
}

/* Called from the opaque pass, after pvr_wait_ready. The previous frame has
 * been rendered by then, so reusing one of its slots is safe. */
void
txr_async_pump(void) {
    char key[ART_KEY_LEN];
    int still_wanted = 0;

    if (!async_thread) {
        return;
    }

    mutex_lock(&async_mutex);
    strcpy(key, result_key);
    for (int i = 0; i < wanted_num; i++) {
        if (!strcmp(wanted[i].key, key)) {
            still_wanted = 1;
        }
    }
    mutex_unlock(&async_mutex);

    if (!key[0]) {
        return;
    }

    if (still_wanted && find_in_cache(&box_system.cache, key) == -1) {
        /* Freshen every wanted entry, so the insert can only evict something else */
        for (int i = wanted_num - 1; i >= 0; i--) {
            find_in_cache(&box_system.cache, wanted[i].key);
        }

        add_to_cache(&box_system.cache, key, 0);
        int slot_num = find_in_cache(&box_system.cache, key);
        if (slot_num != -1) {
            struct image img;
            void* txr_ptr = pool_get_slot_addr(&box_system.pool, slot_num);
            load_pvr_from_buffer_to_buffer(async_buffer, &img.width, &img.height, &img.format, txr_ptr);
            pool_set_slot_format(&box_system.pool, slot_num, img.width, img.height, img.format);
        }
    }

    mutex_lock(&async_mutex);
    result_key[0] = '\0';
    cond_broadcast(&async_cond);
    mutex_unlock(&async_mutex);
}

int
txr_async_busy(void) {
    int busy;

    if (!async_thread) {
        return 0;
    }

    mutex_lock(&async_mutex);
    busy = loading_key[0] != '\0' || async_next_request() >= 0;
    mutex_unlock(&async_mutex);
    return busy;
}

void
txr_async_shutdown(void) {
    async_stopped = 1;
    if (!async_thread) {
        return;
    }

    mutex_lock(&async_mutex);
    async_quit = 1;
    wanted_num = 0;
    cond_broadcast(&async_cond);
    mutex_unlock(&async_mutex);

    thd_join(async_thread, NULL);
    async_thread = NULL;
}
