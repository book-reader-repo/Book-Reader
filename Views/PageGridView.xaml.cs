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

                // Try to find a pre-rendered thumbnail image
                var dir = Path.GetDirectoryName(f) ?? "";
                var imagesDir = Path.Combine(dir, "images");
                if (Directory.Exists(imagesDir) && stepNum >= 0)
                {
                    var imgPath = Path.Combine(imagesDir, $"steps_{stepNum}.jpg");
                    if (!File.Exists(imgPath))
                        imgPath = Path.Combine(imagesDir, $"steps_{stepNum}.png");

                    if (File.Exists(imgPath))
                        thumb.ThumbnailSource = ImageSource.FromFile(imgPath);
                    else
                        thumb.ThumbnailSource = "appicon.png";
                }
                else
                {
                    thumb.ThumbnailSource = "appicon.png";
                }

                _pages.Add(thumb);
            }

            PagesCollection.ItemsSource = _pages;
            StatusText.Text = $"{_pages.Count} pages";
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
