using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GDMENUCardManager.Core.Audio
{
    public class BgmConversionResult
    {
        public TimeSpan Duration { get; set; }
        public long FileSize { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int TrackCount { get; set; } = 1;
    }

    /// <summary>
    /// Converts a WAV/MP3/OGG/FLAC file to the BGM.ADP format openMenu streams at boot.
    /// The file is a 32 byte OMBG header followed by AICA ADPCM data. Stereo packs one
    /// frame per byte with the left sample in the high nibble, matching what the KOS
    /// snd_adpcm_split routine expects. Mono packs two samples per byte, low nibble first.
    ///
    /// A single track is written as version 1. Several tracks are written as version 2:
    /// the header is followed by a table of { u32 offset, u32 length } entries, one per
    /// track, then the tracks themselves. openMenu plays them in random order. All tracks
    /// share one channel count, so mono tracks are upmixed when any track is stereo.
    /// </summary>
    public static class BgmConverter
    {
        public const int TargetSampleRate = 44100;
        private const uint HeaderVersionSingle = 1;
        private const uint HeaderVersionPlaylist = 2;
        private const int HeaderLength = 32;
        private const int TrackEntryLength = 8;
        public const int MaxTracks = 256;
        private const int MaxDurationSeconds = 3600;
        private const int MaxTotalDurationSeconds = 3 * 3600;

        public static Task<BgmConversionResult> ConvertAsync(string sourcePath, string destPath)
        {
            return Task.Run(() => Convert(new[] { sourcePath }, destPath));
        }

        public static Task<BgmConversionResult> ConvertAsync(IReadOnlyList<string> sourcePaths, string destPath)
        {
            return Task.Run(() => Convert(sourcePaths, destPath));
        }

        public static BgmConversionResult Convert(string sourcePath, string destPath)
        {
            return Convert(new[] { sourcePath }, destPath);
        }

        public static BgmConversionResult Convert(IReadOnlyList<string> sourcePaths, string destPath)
        {
            if (sourcePaths == null || sourcePaths.Count == 0)
                throw new ArgumentException("No music file was selected.", nameof(sourcePaths));
            if (sourcePaths.Count > MaxTracks)
                throw new InvalidDataException($"Too many music files selected (maximum {MaxTracks}).");

            // Decode every track first: the channel count of the whole set depends on all of them.
            var tracks = new List<(float[] Samples, int Channels)>();
            long totalFrames = 0;
            foreach (var sourcePath in sourcePaths)
            {
                var name = Path.GetFileName(sourcePath);
                var decoded = AudioDecoder.Decode(sourcePath);
                if (decoded.FrameCount == 0)
                    throw new InvalidDataException($"{name} contains no audio.");
                if (decoded.FrameCount > (long)decoded.SampleRate * MaxDurationSeconds)
                    throw new InvalidDataException($"{name} is longer than an hour, which is too long for menu music.");

                var samples = decoded.Samples;
                int channels = decoded.Channels;
                if (channels > 2)
                {
                    samples = TakeFrontPair(samples, channels);
                    channels = 2;
                }

                samples = Resampler.Resample(samples, channels, decoded.SampleRate, TargetSampleRate);
                if (samples.Length / channels == 0)
                    throw new InvalidDataException($"{name} contains no audio.");

                totalFrames += samples.Length / channels;
                if (totalFrames > (long)TargetSampleRate * MaxTotalDurationSeconds)
                    throw new InvalidDataException("The selected tracks add up to more than three hours, which is too long for menu music.");

                tracks.Add((samples, channels));
            }

            int outChannels = 1;
            foreach (var t in tracks)
                outChannels = Math.Max(outChannels, t.Channels);

            var payloads = new List<byte[]>(tracks.Count);
            for (int i = 0; i < tracks.Count; i++)
            {
                var samples = tracks[i].Samples;
                if (tracks[i].Channels == 1 && outChannels == 2)
                    samples = MonoToStereo(samples);
                int frames = samples.Length / outChannels;
                payloads.Add(outChannels == 2 ? EncodeStereo(samples, frames) : EncodeMono(samples, frames));
                tracks[i] = (null, outChannels); // let the decoded audio go
            }

            bool playlist = payloads.Count > 1;
            long tableLength = playlist ? (long)payloads.Count * TrackEntryLength : 0;
            long dataStart = HeaderLength + tableLength;
            long totalLength = dataStart;
            foreach (var p in payloads)
                totalLength += p.Length;
            if (totalLength > uint.MaxValue)
                throw new InvalidDataException("The selected tracks are too large for menu music.");

            var tempPath = destPath + ".tmp";
            try
            {
                using (var fs = File.Create(tempPath))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(new[] { (byte)'O', (byte)'M', (byte)'B', (byte)'G' });
                    bw.Write(playlist ? HeaderVersionPlaylist : HeaderVersionSingle);
                    bw.Write((uint)TargetSampleRate);
                    bw.Write((ushort)outChannels);
                    bw.Write((ushort)0);
                    bw.Write(playlist ? (uint)payloads.Count : 0u);
                    bw.Write((uint)Math.Min(totalFrames, uint.MaxValue));
                    bw.Write(0ul);

                    if (playlist)
                    {
                        long offset = dataStart;
                        foreach (var p in payloads)
                        {
                            bw.Write((uint)offset);
                            bw.Write((uint)p.Length);
                            offset += p.Length;
                        }
                    }

                    foreach (var p in payloads)
                        bw.Write(p);
                }
                File.Move(tempPath, destPath, overwrite: true);
            }
            catch
            {
                // A leftover tmp inside menu_data would get baked into the menu image.
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }

            return new BgmConversionResult
            {
                Duration = TimeSpan.FromSeconds((double)totalFrames / TargetSampleRate),
                FileSize = totalLength,
                SampleRate = TargetSampleRate,
                Channels = outChannels,
                TrackCount = payloads.Count
            };
        }

        private static float[] MonoToStereo(float[] mono)
        {
            var stereo = new float[mono.Length * 2];
            for (int i = 0; i < mono.Length; i++)
            {
                stereo[i * 2] = mono[i];
                stereo[i * 2 + 1] = mono[i];
            }
            return stereo;
        }

        private static byte[] EncodeStereo(float[] interleaved, int frames)
        {
            int padded = Pad32(frames);
            var payload = new byte[padded];
            var left = new AicaAdpcmEncoder();
            var right = new AicaAdpcmEncoder();

            for (int i = 0; i < frames; i++)
            {
                int ln = left.EncodeSample(ToPcm16(interleaved[i * 2]));
                int rn = right.EncodeSample(ToPcm16(interleaved[i * 2 + 1]));
                payload[i] = (byte)((ln << 4) | rn);
            }
            for (int i = frames; i < padded; i++)
            {
                int ln = left.EncodeSample(0);
                int rn = right.EncodeSample(0);
                payload[i] = (byte)((ln << 4) | rn);
            }
            return payload;
        }

        private static byte[] EncodeMono(float[] samples, int frames)
        {
            // Two samples per byte, so pad the sample count to a 64 sample boundary.
            int paddedFrames = Pad32((frames + 1) / 2) * 2;
            var payload = new byte[paddedFrames / 2];
            var enc = new AicaAdpcmEncoder();

            for (int i = 0; i < paddedFrames; i++)
            {
                int nibble = enc.EncodeSample(i < frames ? ToPcm16(samples[i]) : 0);
                if ((i & 1) == 0)
                    payload[i / 2] = (byte)nibble;
                else
                    payload[i / 2] |= (byte)(nibble << 4);
            }
            return payload;
        }

        private static int Pad32(int byteCount)
        {
            return (byteCount + 31) & ~31;
        }

        private static int ToPcm16(float sample)
        {
            int v = (int)Math.Round(sample * 32768f);
            if (v > short.MaxValue)
                v = short.MaxValue;
            else if (v < short.MinValue)
                v = short.MinValue;
            return v;
        }

        private static float[] TakeFrontPair(float[] interleaved, int channels)
        {
            int frames = interleaved.Length / channels;
            var stereo = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                stereo[i * 2] = interleaved[i * channels];
                stereo[i * 2 + 1] = interleaved[i * channels + 1];
            }
            return stereo;
        }
    }
}
