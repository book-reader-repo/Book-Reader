using BookViewer.Models;
using BookViewer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls;

namespace BookViewer.Views
{
    public partial class UnitPage : ContentPage
    {
        private readonly string _bookFolder;
        private readonly BookData _bookData;
        private readonly UnitData _unit;
        private readonly ResourceService _resourceService = new();
        private List<ResourceDisplay> _allResources = new();

        public class ResourceDisplay
        {
            public string Type { get; set; } = "";
            public string TypeIcon { get; set; } = "";
            public string Description { get; set; } = "";
            public string PageNumber { get; set; } = "";
            public string Path { get; set; } = "";
        }

        public UnitPage(string bookFolder, BookData bookData, UnitData unit)
        {
            InitializeComponent();
            NavigationPage.SetHasNavigationBar(this, false);
            _bookFolder = bookFolder;
            _bookData = bookData;
            _unit = unit;
        
            UnitNumberLabel.Text = ExtractUnitNumber(unit.Title);
            UnitTitleLabel.Text = ExtractUnitTitle(unit.Title);
        
            LoadSections();
            LoadResources();
            LoadUnitBanner();   // NEW
        }
        private void LoadUnitBanner()
        {
            try
            {
                // unit.Id is like "unitUID_18657" so the file is unitUID_18657.png
                // Look in [book_dir]/units/ first, then search recursively
                string bannerPath = null;
        
                var unitsDir = Path.Combine(_bookFolder, "units");
                if (Directory.Exists(unitsDir))
                {
                    var candidate = Path.Combine(unitsDir, $"{_unit.Id}.png");
                    if (File.Exists(candidate)) bannerPath = candidate;
                }
        
                if (bannerPath == null)
                {
                    // Search all subfolders in case structure differs
                    var matches = Directory.GetFiles(_bookFolder, $"{_unit.Id}.png", SearchOption.AllDirectories);
                    if (matches.Length > 0) bannerPath = matches[0];
                }
        
                if (!string.IsNullOrEmpty(bannerPath) && File.Exists(bannerPath))
                {
                    UnitBannerImage.Source = ImageSource.FromFile(bannerPath);
                    UnitBannerImage.IsVisible = true;
                    UnitHeroFallback.IsVisible = false;
                }
                else
                {
                    UnitBannerImage.IsVisible = false;
                    UnitHeroFallback.IsVisible = true;
                    UnitNumberLabel.Text = ExtractUnitNumber(_unit.Title);
                    UnitTitleLabel.Text = ExtractUnitTitle(_unit.Title);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading unit banner: {ex.Message}");
                UnitBannerImage.IsVisible = false;
                UnitHeroFallback.IsVisible = true;
                UnitNumberLabel.Text = ExtractUnitNumber(_unit.Title);
                UnitTitleLabel.Text = ExtractUnitTitle(_unit.Title);
            }
        }

        private string ExtractUnitNumber(string title)
        {
            var m = Regex.Match(title ?? "", @"(\d+)");
            return m.Success ? m.Groups[1].Value : "1";
        }

        private string ExtractUnitTitle(string title)
        {
            return Regex.Replace(title ?? "", @"^Unit\s*\d+\s*", "", RegexOptions.IgnoreCase).Trim();
        }

        private void LoadSections()
        {
            var sections = _bookData.GetSectionsForUnit(_unit.Id);
            SectionsCollection.ItemsSource = sections;
        }
        
        private void LoadResources()
        {
            _allResources.Clear();
        
            var sections = _bookData.GetSectionsForUnit(_unit.Id);
            if (sections == null || sections.Count == 0)
            {
                ResourcesCollection.ItemsSource = _allResources;
                return;
            }
        
            foreach (var section in sections)
            {
                foreach (var r in section.Resources)
                {
                    _allResources.Add(new ResourceDisplay
                    {
                        Type = r.Type,
                        TypeIcon = GetIconForType(r.Type),
                        Description = r.Description,
                        PageNumber = r.PageNumber,
                        Path = r.Path
                    });
                }
            }
        
            // Sort by page number, then by description
            _allResources = _allResources
                .OrderBy(r => int.TryParse(r.PageNumber, out var n) ? n : 0)
                .ThenBy(r => r.Description)
                .ToList();
        
            ResourcesCollection.ItemsSource = _allResources;
        }

        private string GetIconForType(string type)
        {
            return type switch
            {
                "audio" => "🔊",
                "video" => "🎬",
                "pdf" => "📄",
                "doc" => "📝",
                "web" => "🌐",
                _ => "📎"
            };
        }

        private int ParseStepIndex(string fileName)
        {
            var m = Regex.Match(fileName ?? "", @"^steps?_(\d+)\.html$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int idx)) return idx;
            return -1;
        }

        private async void OnBackClicked(object sender, EventArgs e)
        {
            await Navigation.PopAsync();
        }
        private void OnResourcesTabTapped(object sender, EventArgs e)
        {
            ContentsView.IsVisible = false;
            ResourcesView.IsVisible = true;
            ResourcesTab.BackgroundColor = Color.FromArgb("#E0E0E0");
            ResourcesTab.TextColor = Colors.Black;
            ContentsTab.BackgroundColor = Colors.Transparent;
            ContentsTab.TextColor = Color.FromArgb("#666666");
        }
                
        private async void OnSectionTapped(object sender, EventArgs e)
        {
            if (((Grid)sender).BindingContext is SectionData section)
            {
                int folio = 1;
                if (int.TryParse(section.PageStart, out int f) && f > 0)
                    folio = f;
        
                var viewer = new BookViewerPage(_bookFolder, section.Id, folio);
                await Navigation.PushAsync(viewer);
            }
        }

        private async void OnResourceSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not ResourceDisplay r) return;
            ResourcesCollection.SelectedItem = null;

            try
            {
                if (r.Path.StartsWith("http"))
                {
                    await Launcher.Default.OpenAsync(r.Path);
                }
                else if (File.Exists(r.Path))
                {
                    await Launcher.Default.OpenAsync(new OpenFileRequest
                    {
                        File = new ReadOnlyFile(r.Path)
                    });
                }
                else
                {
                    await DisplayAlert("Not found", $"Resource not found:\n{r.Path}", "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }
    }
}
