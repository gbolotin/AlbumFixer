using CommunityToolkit.Mvvm.ComponentModel;
using WpfFoundation.Theming;

namespace AlbumFixer.App;

/// <summary>The theme choice: System follows Windows, and Light or Dark override it. The choice is remembered.</summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly IThemeService _themes;
    private readonly ThemePreferenceStore _store;
    private readonly bool _loaded;

    public AppearanceViewModel(IThemeService themes, ThemePreferenceStore store)
    {
        _themes = themes;
        _store = store;
        SelectedTheme = store.Load();
        themes.ApplyTheme(SelectedTheme);
        _loaded = true;
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = [ThemePreference.System, ThemePreference.Light, ThemePreference.Dark];

    [ObservableProperty]
    public partial ThemePreference SelectedTheme { get; set; }

    partial void OnSelectedThemeChanged(ThemePreference value)
    {
        _themes.ApplyTheme(value);
        // Loading the saved choice applies it without saving it again.
        if (_loaded)
        {
            _store.Save(value);
        }
    }
}
