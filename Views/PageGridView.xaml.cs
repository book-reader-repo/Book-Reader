using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace BookViewer.Views
{
    public partial class PageGridView : ContentPage
    {
        public class PageThumbnail
        {
            public int Index { get; set; }
            public string PageNumber { get; set; } = "";
            public ImageSource ThumbnailSource { get; set; }
            public string HtmlPath { get; set; } = "";
        }

        private readonly List<PageThumbnail> _pages = new();
        private readonly List<string> _pageFiles;
        private readonly Action<int> _onPageSelected;

        public PageGridView(List<string> pageFiles, int currentIndex, Action<int> onPageSelected)
        {
            InitializeComponent();
            NavigationPage.SetHasNavigationBar(this, false);
            _pageFiles = pageFiles;
            _onPageSelected = onPageSelected;

            LoadThumbnails(currentIndex);
        }
        private async void OnPageTapped(object sender, TappedEventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is PageThumbnail page)
            {
                var targetIndex = page.Index;
                await Navigation.PopAsync();       // pop first
                _onPageSelected?.Invoke(targetIndex);  // then tell viewer to load
            }
        }
        private async void LoadThumbnails(int currentIndex)
        {
            for (int i = 0; i < _pageFiles.Count; i++)
            {
                var f = _pageFiles[i];
                var stepNum = ParseStepIndex(Path.GetFileName(f));
        
                var thumb = new PageThumbnail
                {
                    Index = i,
                    PageNumber = stepNum >= 0 ? stepNum.ToString() : (i + 1).ToString(),
                    HtmlPath = f
                };
        
                // Thumbnails live in [folder-of-steps_x.html]/thumbs/steps_x.jpg
                var dir = Path.GetDirectoryName(f) ?? "";
                var thumbsDir = Path.Combine(dir, "thumbs");
        
                if (Directory.Exists(thumbsDir) && stepNum >= 0)
                {
                    var jpg = Path.Combine(thumbsDir, $"steps_{stepNum}.jpg");
                    var png = Path.Combine(thumbsDir, $"steps_{stepNum}.png");
                    var jpgAlt = Path.Combine(thumbsDir, $"step_{stepNum}.jpg");
                    var pngAlt = Path.Combine(thumbsDir, $"step_{stepNum}.png");
        
                    if (File.Exists(jpg)) thumb.ThumbnailSource = ImageSource.FromFile(jpg);
                    else if (File.Exists(png)) thumb.ThumbnailSource = ImageSource.FromFile(png);
                    else if (File.Exists(jpgAlt)) thumb.ThumbnailSource = ImageSource.FromFile(jpgAlt);
                    else if (File.Exists(pngAlt)) thumb.ThumbnailSource = ImageSource.FromFile(pngAlt);
                    else thumb.ThumbnailSource = "appicon.png";
                }
                else
                {
                    thumb.ThumbnailSource = "appicon.png";
                }
        
                _pages.Add(thumb);
            }
        
            PagesCollection.ItemsSource = _pages;
            StatusText.Text = $"{_pages.Count}";
        }
        private int ParseStepIndex(string fileName)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                fileName ?? "", @"^steps?_(\d+)\.html$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int idx)) return idx;
            return -1;
        }

        private async void OnPageSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.Count == 0) return;
            if (e.CurrentSelection[0] is not PageThumbnail p) return;

            _onPageSelected?.Invoke(p.Index);
            await Navigation.PopAsync();
        }

        private async void OnCloseClicked(object sender, EventArgs e)
        {
            await Navigation.PopAsync();
        }
    }
}
