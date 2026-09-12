using BookViewer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls;

namespace BookViewer.Views
{
    public partial class LibraryPage : ContentPage
    {
        public class LibraryBook
        {
            public string FolderPath { get; set; } = "";
            public string BookId { get; set; } = "";
            public string Title { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public ImageSource CoverImageSource { get; set; }
            public BookData Data { get; set; }
        }

        private readonly List<LibraryBook> _books = new();

        public LibraryPage()
        {
            InitializeComponent();
            LoadBooks();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            LoadBooks();
        }

        private void LoadBooks()
        {
            _books.Clear();

            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var booksDir = Path.Combine(documentsPath, "BookViewer", "Books");

            if (Directory.Exists(booksDir))
            {
                foreach (var dir in Directory.GetDirectories(booksDir))
                {
                    var bookXmlPath = Path.Combine(dir, "book.xml");
                    if (!File.Exists(bookXmlPath)) continue;

                    try
                    {
                        var content = File.ReadAllText(bookXmlPath);
                        var book = new LibraryBook { FolderPath = dir };
                        var data = new BookData();
                        data.LoadFromXml(content, dir);
                        book.Data = data;
                        book.BookId = data.BookId;
                        book.Title = string.IsNullOrEmpty(data.Title) ? Path.GetFileName(dir) : data.Title;
                        book.DisplayName = book.Title;

                        // Find cover image
                        string cover = FindCoverImage(dir, book.BookId);
                        if (!string.IsNullOrEmpty(cover) && File.Exists(cover))
                            book.CoverImageSource = ImageSource.FromFile(cover);
                        else
                            book.CoverImageSource = "appicon.png"; // fallback

                        _books.Add(book);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading book: {ex.Message}");
                    }
                }
            }

            BooksCollection.ItemsSource = _books;
            EmptyState.IsVisible = _books.Count == 0;
        }

        private string FindCoverImage(string dir, string bookId)
        {
            var candidates = new[]
            {
                Path.Combine(dir, $"{bookId}.png"),
                Path.Combine(dir, $"book_{bookId}.png"),
                Path.Combine(dir, "cover.png"),
                Path.Combine(dir, "cover.jpg"),
                Path.Combine(dir, "images", $"book_{bookId}.png"),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private async void OnBookSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not LibraryBook book) return;
            BooksCollection.SelectedItem = null;

            var tocPage = new TocPage(book.FolderPath, book.Data);
            await Navigation.PushAsync(tocPage);
        }
    }
}
