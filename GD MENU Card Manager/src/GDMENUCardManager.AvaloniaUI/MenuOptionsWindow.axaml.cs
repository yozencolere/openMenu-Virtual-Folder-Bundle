using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ByteSizeLib;
using GDMENUCardManager.Core.MenuOptions;
using MsBox.Avalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace GDMENUCardManager
{
    public partial class MenuOptionsWindow : Window, INotifyPropertyChanged
    {
        private readonly MenuOptionsManager _menuOptions;
        private readonly MenuOptionsConfig _config;

        public ObservableCollection<ThemeEntry> Themes { get; } = new ObservableCollection<ThemeEntry>();

        private ThemeEntry _selectedTheme;
        public ThemeEntry SelectedTheme
        {
            get => _selectedTheme;
            set { _selectedTheme = value; RaisePropertyChanged(); }
        }

        private bool _forceStyleTheme;
        public bool ForceStyleTheme
        {
            get => _forceStyleTheme;
            set { _forceStyleTheme = value; RaisePropertyChanged(); }
        }

        private MenuStyle _style = MenuStyle.Folders;

        // Manage radio state in code, the group unchecking clobbers the IsChecked binding.
        private RadioButton _radioFolders;
        private RadioButton _radioScroll;
        private RadioButton _radioGrid3;
        private RadioButton _radioLineDesc;

        private bool _bgmEnabled;
        public bool BgmEnabled
        {
            get => _bgmEnabled;
            set
            {
                _bgmEnabled = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanApplyBgm));
                RaisePropertyChanged(nameof(ShowBgmHint));
            }
        }

        private string _selectedSourcePath;
        public string SelectedSourcePath
        {
            get => _selectedSourcePath;
            set
            {
                _selectedSourcePath = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SelectedSourceDisplay));
                RaisePropertyChanged(nameof(CanApplyBgm));
                RaisePropertyChanged(nameof(ShowBgmHint));
            }
        }

        private IReadOnlyList<string> _selectedSourcePaths;
        /// <summary>Every file picked in the last Browse, in the order given.</summary>
        public IReadOnlyList<string> SelectedSourcePaths
        {
            get => _selectedSourcePaths;
            set
            {
                _selectedSourcePaths = value;
                SelectedSourcePath = value != null && value.Count > 0 ? value[0] : null;
            }
        }

        public string SelectedSourceDisplay =>
            string.IsNullOrEmpty(SelectedSourcePath) ? "(no new file selected)"
            : _selectedSourcePaths != null && _selectedSourcePaths.Count > 1
                ? $"{_selectedSourcePaths.Count} files: " + string.Join(", ", _selectedSourcePaths.Select(Path.GetFileName))
                : SelectedSourcePath;

        private bool _hasExistingBgm;
        public bool HasExistingBgm
        {
            get => _hasExistingBgm;
            private set { _hasExistingBgm = value; RaisePropertyChanged(); }
        }

        private string _bgmCurrentText;
        public string BgmCurrentText
        {
            get => _bgmCurrentText;
            private set { _bgmCurrentText = value; RaisePropertyChanged(); }
        }

        public bool CanApplyBgm => !BgmEnabled || HasExistingBgm || !string.IsNullOrEmpty(SelectedSourcePath);

        public bool ShowBgmHint => !CanApplyBgm;

        private bool _serialSdWarning;
        public bool SerialSdWarning
        {
            get => _serialSdWarning;
            set { _serialSdWarning = value; RaisePropertyChanged(); }
        }

        public new event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public MenuOptionsWindow()
        {
            InitializeComponent();
        }

        public MenuOptionsWindow(GDMENUCardManager.Core.Manager manager) : this()
        {
            _radioFolders = this.FindControl<RadioButton>("RadioStyleFolders");
            _radioScroll = this.FindControl<RadioButton>("RadioStyleScroll");
            _radioGrid3 = this.FindControl<RadioButton>("RadioStyleGrid3");
            _radioLineDesc = this.FindControl<RadioButton>("RadioStyleLineDesc");

            _menuOptions = manager.CreateMenuOptionsManager();
            _config = _menuOptions.Load();

            _forceStyleTheme = _config.ForceStyleTheme;
            _style = _config.Style;
            _bgmEnabled = _config.BgmEnabled;
            _serialSdWarning = _config.SerialSdWarning;
            UpdateStyleRadios();
            RefreshBgmState(_config);

            RefreshThemes(_config.ThemeId);
            DataContext = this;
            KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape && IsEnabled)
                {
                    e.Handled = true;
                    Close();
                }
            };

            // Nothing closes the window while an Apply is running.
            Closing += (s, e) =>
            {
                if (!IsEnabled && e.CloseReason == WindowCloseReason.WindowClosing)
                    e.Cancel = true;
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void StyleRadio_Click(object sender, RoutedEventArgs e)
        {
            if (sender == _radioScroll)
                SetStyle(MenuStyle.Scroll);
            else if (sender == _radioGrid3)
                SetStyle(MenuStyle.Grid3);
            else if (sender == _radioLineDesc)
                SetStyle(MenuStyle.LineDesc);
            else
                SetStyle(MenuStyle.Folders);
            UpdateStyleRadios();
        }

        private void UpdateStyleRadios()
        {
            _radioFolders.IsChecked = _style == MenuStyle.Folders;
            _radioScroll.IsChecked = _style == MenuStyle.Scroll;
            _radioGrid3.IsChecked = _style == MenuStyle.Grid3;
            _radioLineDesc.IsChecked = _style == MenuStyle.LineDesc;
        }

        private void SetStyle(MenuStyle style)
        {
            if (_style == style)
                return;
            _style = style;
            RefreshThemes(null);
        }

        private void RefreshThemes(string preferredThemeId)
        {
            var themes = _menuOptions.GetThemesForStyle(_style);
            Themes.Clear();
            foreach (var t in themes)
                Themes.Add(t);

            SelectedTheme = Themes.FirstOrDefault(t => t.Id == preferredThemeId) ?? Themes.FirstOrDefault();
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var pickedFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Music Files",
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new FilePickerFileType("Audio Files")
                    {
                        Patterns = new List<string> { "*.wav", "*.mp3", "*.ogg", "*.flac" }
                    },
                    new FilePickerFileType("All Files")
                    {
                        // Use "*.*" here, a plain "*" ends up matching nothing on macOS.
                        Patterns = new List<string> { "*.*" },
                        AppleUniformTypeIdentifiers = new List<string> { "public.item" }
                    }
                }
            });
            var selectedFiles = pickedFiles
                .Select(f => f.TryGetLocalPath())
                .Where(p => p != null)
                .ToList();
            if (selectedFiles.Count > 0)
            {
                SelectedSourcePaths = selectedFiles;
            }
        }

        private void RefreshBgmState(MenuOptionsConfig cfg)
        {
            HasExistingBgm = cfg.BgmFileExists;
            BgmCurrentText = !cfg.BgmFileExists ? null
                : string.IsNullOrEmpty(cfg.BgmSourceFile)
                    ? "Current: BGM.ADP"
                    : $"Current: {cfg.BgmSourceFile} (converted {cfg.BgmConvertedDate})";
            RaisePropertyChanged(nameof(CanApplyBgm));
            RaisePropertyChanged(nameof(ShowBgmHint));
        }

        private async void ApplyStyleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IsEnabled = false;
                await _menuOptions.ApplyStyleThemeAsync(ForceStyleTheme, _style,
                    SelectedTheme?.Id ?? MenuOptionsManager.DefaultThemeId(_style));

                await MessageBoxManager.GetMessageBoxStandard("Menu Options",
                    "Default style and theme settings applied.\n\nChanges take effect after clicking \"Save Changes\" in the main window.",
                    icon: MsBox.Avalonia.Enums.Icon.None, windowStartupLocation: WindowStartupLocation.CenterOwner).ShowWindowDialogAsync(this);
            }
            catch (Exception ex)
            {
                await MessageBoxManager.GetMessageBoxStandard("Error", ex.Message,
                    icon: MsBox.Avalonia.Enums.Icon.None, windowStartupLocation: WindowStartupLocation.CenterOwner).ShowWindowDialogAsync(this);
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private async void ApplyBgmButton_Click(object sender, RoutedEventArgs e)
        {
            var newSources = BgmEnabled && SelectedSourcePaths != null && SelectedSourcePaths.Count > 0
                ? SelectedSourcePaths : null;
            var newSource = newSources?[0];

            ProgressWindow progressWindow = null;
            if (!string.IsNullOrEmpty(newSource))
            {
                progressWindow = new ProgressWindow();
                progressWindow.Title = "Converting Music";
                progressWindow.TotalItems = 1;
                progressWindow.TextContent = newSources.Count > 1
                    ? $"Converting {newSources.Count} tracks to Dreamcast ADPCM..."
                    : $"Converting {Path.GetFileName(newSource)} to Dreamcast ADPCM...";
                progressWindow.Show(this);
            }

            try
            {
                IsEnabled = false;
                var result = await _menuOptions.ApplyBgmAsync(BgmEnabled, newSources);

                if (progressWindow != null)
                {
                    progressWindow.AllowClose();
                    progressWindow.Close();
                    progressWindow = null;
                }

                RefreshBgmState(_menuOptions.Load());
                SelectedSourcePaths = null;

                var message = "Background music setting applied.";
                if (result != null)
                {
                    var size = ByteSize.FromBytes(result.FileSize);
                    var mode = result.Channels == 2 ? "stereo" : "mono";
                    var duration = $"{(int)result.Duration.TotalMinutes}:{result.Duration.Seconds:00}";
                    message = result.TrackCount > 1
                        ? $"{result.TrackCount} music tracks converted ({duration} in total, {mode}, {size:0.#}). openMenu plays them in random order."
                        : $"Music track converted ({duration}, {mode}, {size:0.#}).";
                    if (result.Duration.TotalMinutes > 10)
                        message += "\n\nNote: tracks over 10 minutes make the menu disc image noticeably larger.";
                }
                message += "\n\nChanges take effect after clicking \"Save Changes\" in the main window.";

                await MessageBoxManager.GetMessageBoxStandard("Menu Options", message,
                    icon: MsBox.Avalonia.Enums.Icon.None, windowStartupLocation: WindowStartupLocation.CenterOwner).ShowWindowDialogAsync(this);
            }
            catch (Exception ex)
            {
                await MessageBoxManager.GetMessageBoxStandard("Error", ex.Message,
                    icon: MsBox.Avalonia.Enums.Icon.None, windowStartupLocation: WindowStartupLocation.CenterOwner).ShowWindowDialogAsync(this);
            }
            finally
            {
                IsEnabled = true;
                if (progressWindow != null)
                {
                    progressWindow.AllowClose();
                    progressWindow.Close();
                }
            }
        }

        private async void ApplyMiscButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IsEnabled = false;
                await _menuOptions.ApplyMiscAsync(SerialSdWarning);

                await MessageBoxManager.GetMessageBoxStandard("Menu Options",
                    "Miscellaneous settings applied.\n\nChanges take effect after clicking \"Save Changes\" in the main window.",
                    icon: MsBox.Avalonia.Enums.Icon.None, windowStartupLocation: WindowStartupLocation.CenterOwner).ShowWindowDialogAsync(this);
            }
            catch (Exception ex)
            {
                await MessageBoxManager.GetMessageBoxStandard("Error", ex.Message,
                    icon: MsBox.Avalonia.Enums.Icon.None, windowStartupLocation: WindowStartupLocation.CenterOwner).ShowWindowDialogAsync(this);
            }
            finally
            {
                IsEnabled = true;
            }
        }

    }
}
