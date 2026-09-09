using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BookViewer.Services
{
    public class DownloadService
    {
        private const string BaseUrl = "https://isolution.oupchina.com.hk";
        private readonly HttpClient _httpClient;
        private bool _isDownloading;

        public event EventHandler<int> OnProgress;
        public event EventHandler<string> OnComplete;
        public event EventHandler<string> OnError;

        public DownloadService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "BookViewer/1.0");
        }

        public async Task DownloadBookAsync(int bookNumber)
        {
            if (_isDownloading)
            {
                OnError?.Invoke(this, "Download already in progress");
                return;
            }

            _isDownloading = true;
            try
            {
                OnProgress?.Invoke(this, 0);

                // Get the books directory
                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var booksDir = Path.Combine(documentsPath, "BookViewer", "Books");
                Directory.CreateDirectory(booksDir);

                var tempDir = Path.Combine(booksDir, "temp");
                Directory.CreateDirectory(tempDir);

                // Final book directory
                var finalBookDir = Path.Combine(booksDir, $"book_{bookNumber}");
                
                // If the book already exists, ask for confirmation
                if (Directory.Exists(finalBookDir))
                {
                    OnError?.Invoke(this, $"Book {bookNumber} already exists. Please delete it first.");
                    _isDownloading = false;
                    return;
                }

                var urlPatterns = new[]
                {
                    $"ebook_distribute/V3_otf/{bookNumber}/P02/book_{bookNumber}_P02.zip",
                    $"ebook_distribute/V3_otf/{bookNumber}/book_{bookNumber}.zip",
                };

                bool bookDownloaded = false;
                string bookExtractDir = null;
                string parentDir = null;

                for (int i = 0; i < urlPatterns.Length; i++)
                {
                    if (bookDownloaded) break;

                    var pattern = urlPatterns[i];
                    var url = $"{BaseUrl}/{pattern}";
                    var savePath = Path.Combine(tempDir, $"book_{bookNumber}.zip");

                    OnProgress?.Invoke(this, 10 + (i * 20));

                    if (await DownloadFileAsync(url, savePath))
                    {
                        if (await VerifyZipAsync(savePath))
                        {
                            bookExtractDir = Path.Combine(tempDir, $"book_{bookNumber}");
                            Directory.CreateDirectory(bookExtractDir);

                            OnProgress?.Invoke(this, 40);
                            var unitUids = await ExtractAndFindUidsAsync(savePath, bookExtractDir);

                            parentDir = pattern.Contains("/P02/")
                                ? pattern.Split("/P02/")[0] + "/P02"
                                : pattern.Substring(0, pattern.LastIndexOf('/'));

                            File.Delete(savePath);
                            bookDownloaded = true;

                            OnProgress?.Invoke(this, 50);
                            await DownloadBookResourcePCAsync(bookNumber, parentDir, bookExtractDir);

                            OnProgress?.Invoke(this, 60);
                            if (unitUids.Any())
                            {
                                await ProcessUnitsAsync(unitUids, parentDir, bookNumber, bookExtractDir);
                            }

                            OnProgress?.Invoke(this, 80);
                            
                            // Create the final directory and copy everything
                            Directory.CreateDirectory(finalBookDir);
                            CopyDirectory(bookExtractDir, finalBookDir);

                            // Try to find and copy the book cover image
                            await CopyBookCoverAsync(bookNumber, finalBookDir);

                            // Delete temp folder
                            try { Directory.Delete(bookExtractDir, true); } catch { }

                            OnProgress?.Invoke(this, 100);
                            OnComplete?.Invoke(this, finalBookDir);
                            _isDownloading = false;
                            return;
                        }
                        else
                        {
                            File.Delete(savePath);
                        }
                    }
                }

                if (!bookDownloaded)
                {
                    OnError?.Invoke(this, $"Failed to download book {bookNumber}");
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Download failed: {ex.Message}");
            }
            finally
            {
                _isDownloading = false;
            }
        }

        private async Task CopyBookCoverAsync(int bookNumber, string bookDir)
        {
            try
            {
                // Look for cover image in various locations
                string[] possibleCoverPaths = new[]
                {
                    Path.Combine(bookDir, $"{bookNumber}.png"),
                    Path.Combine(bookDir, $"book_{bookNumber}.png"),
                    Path.Combine(bookDir, "cover.png"),
                    Path.Combine(bookDir, "cover.jpg"),
                    Path.Combine(bookDir, "images", $"book_{bookNumber}.png"),
                    Path.Combine(bookDir, "images", $"{bookNumber}.png"),
                    Path.Combine(bookDir, "resource", $"book_{bookNumber}.png"),
                };

                string foundCover = null;
                foreach (var path in possibleCoverPaths)
                {
                    if (File.Exists(path))
                    {
                        foundCover = path;
                        break;
                    }
                }

                // If no cover found, try to download it
                if (string.IsNullOrEmpty(foundCover))
                {
                    string coverUrl = $"{BaseUrl}/ebook_distribute/V3_otf/{bookNumber}/P02/{bookNumber}.png";
                    string coverPath = Path.Combine(bookDir, $"{bookNumber}.png");
                    
                    if (await DownloadFileAsync(coverUrl, coverPath))
                    {
                        foundCover = coverPath;
                        Log($"Downloaded cover image for book {bookNumber}");
                    }
                    else
                    {
                        // Try alternative URL
                        coverUrl = $"{BaseUrl}/ebook_distribute/V3_otf/{bookNumber}/book_{bookNumber}.png";
                        if (await DownloadFileAsync(coverUrl, coverPath))
                        {
                            foundCover = coverPath;
                            Log($"Downloaded cover image for book {bookNumber} from alternative URL");
                        }
                    }
                }
                else
                {
                    // Copy the found cover to the root with the book number name
                    string destPath = Path.Combine(bookDir, $"{bookNumber}.png");
                    if (foundCover != destPath)
                    {
                        File.Copy(foundCover, destPath, true);
                        foundCover = destPath;
                    }
                    Log($"Found cover image for book {bookNumber}: {foundCover}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error copying book cover: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[DownloadService] {message}");
        }

        private async Task<bool> DownloadFileAsync(string url, string savePath)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    return false;

                var content = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(savePath, content);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> VerifyZipAsync(string filePath)
        {
            try
            {
                var buffer = new byte[4];
                using var fs = File.OpenRead(filePath);
                await fs.ReadAsync(buffer, 0, 4);
                return buffer[0] == 0x50 && buffer[1] == 0x4B;
            }
            catch
            {
                return false;
            }
        }

        private async Task<List<string>> ExtractAndFindUidsAsync(string zipPath, string extractDir)
        {
            var unitUids = new HashSet<string>();

            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    var fullPath = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    if (!entry.FullName.EndsWith("/"))
                    {
                        entry.ExtractToFile(fullPath, true);
                    }
                }

                // Find unit UIDs from PNG files
                var unitsDir = Path.Combine(extractDir, "units");
                if (Directory.Exists(unitsDir))
                {
                    foreach (var file in Directory.GetFiles(unitsDir, "*.png"))
                    {
                        var match = Regex.Match(Path.GetFileNameWithoutExtension(file), @"unitUID_(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int num) && num >= 1000 && num <= 100000)
                            unitUids.Add(match.Groups[1].Value);
                    }
                }

                // Find unit UIDs from metadata files
                foreach (var file in Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".json") || file.EndsWith(".xml") || file.EndsWith(".txt"))
                    {
                        try
                        {
                            var content = await File.ReadAllTextAsync(file);
                            var matches = Regex.Matches(content, @"unitUID[_:]?\s*[=:]?\s*[""']?(\d{4,6})[""']?");
                            foreach (Match match in matches)
                            {
                                if (int.TryParse(match.Groups[1].Value, out int num) && num >= 1000 && num <= 100000)
                                    unitUids.Add(match.Groups[1].Value);
                            }
                        }
                        catch { }
                    }
                }

                return unitUids.ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private async Task DownloadBookResourcePCAsync(int bookNumber, string parentDir, string extractDir)
        {
            var patterns = new[]
            {
                $"{parentDir}/book_{bookNumber}_resource_pc.zip",
                $"ebook_distribute/V3_otf/{bookNumber}/P02/book_{bookNumber}_resource_pc.zip",
            };

            var resourceDir = Path.Combine(extractDir, "resource");
            Directory.CreateDirectory(resourceDir);

            foreach (var pattern in patterns)
            {
                var url = $"{BaseUrl}/{pattern}";
                var savePath = Path.Combine(Path.GetTempPath(), $"book_{bookNumber}_pc.zip");

                if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                {
                    try
                    {
                        using var zip = ZipFile.OpenRead(savePath);
                        foreach (var entry in zip.Entries)
                        {
                            var fullPath = Path.Combine(resourceDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                            var directory = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(directory))
                                Directory.CreateDirectory(directory);

                            if (!entry.FullName.EndsWith("/"))
                            {
                                entry.ExtractToFile(fullPath, true);
                            }
                        }
                    }
                    catch { }

                    try { File.Delete(savePath); } catch { }
                    break;
                }
            }
        }

        private async Task ProcessUnitsAsync(List<string> unitUids, string parentDir, int bookNumber, string extractDir)
        {
            var resourceDir = Path.Combine(extractDir, "resource");
            Directory.CreateDirectory(resourceDir);

            for (int i = 0; i < unitUids.Count; i++)
            {
                var uid = unitUids[i];
                OnProgress?.Invoke(this, 60 + (int)((i / (double)unitUids.Count) * 20));

                var unitDir = await DownloadUnitAsync(uid, parentDir, bookNumber);
                if (unitDir != null)
                {
                    // Copy to book root
                    foreach (var item in Directory.GetFileSystemEntries(unitDir))
                    {
                        var name = Path.GetFileName(item);
                        var dest = Path.Combine(extractDir, name);

                        if (File.Exists(dest))
                        {
                            var baseName = Path.GetFileNameWithoutExtension(name);
                            var ext = Path.GetExtension(name);
                            dest = Path.Combine(extractDir, $"{baseName}_unit{uid}{ext}");
                        }

                        if (Directory.Exists(item))
                            CopyDirectory(item, dest);
                        else
                            File.Copy(item, dest, true);
                    }

                    // Download PC resource for unit
                    var pcDir = await DownloadUnitPCAsync(uid, parentDir, bookNumber);
                    if (pcDir != null)
                    {
                        foreach (var item in Directory.GetFileSystemEntries(pcDir))
                        {
                            var name = Path.GetFileName(item);
                            var dest = Path.Combine(resourceDir, name);

                            if (File.Exists(dest))
                            {
                                var baseName = Path.GetFileNameWithoutExtension(name);
                                var ext = Path.GetExtension(name);
                                dest = Path.Combine(resourceDir, $"{baseName}_pc{uid}{ext}");
                            }

                            if (Directory.Exists(item))
                                CopyDirectory(item, dest);
                            else
                                File.Copy(item, dest, true);
                        }

                        try { Directory.Delete(pcDir, true); } catch { }
                    }

                    try { Directory.Delete(unitDir, true); } catch { }
                }
            }
        }

        private async Task<string> DownloadUnitAsync(string uid, string parentDir, int bookNumber)
        {
            var patterns = new[]
            {
                $"{parentDir}/unitUID_{uid}.zip",
                $"{parentDir}/unit_{uid}.zip",
            };

            var extractDir = Path.Combine(Path.GetTempPath(), $"unit_{uid}");

            foreach (var pattern in patterns)
            {
                var url = $"{BaseUrl}/{pattern}";
                var savePath = Path.Combine(Path.GetTempPath(), $"unit_{uid}.zip");

                if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                {
                    try
                    {
                        Directory.CreateDirectory(extractDir);
                        using var zip = ZipFile.OpenRead(savePath);
                        foreach (var entry in zip.Entries)
                        {
                            var fullPath = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                            var directory = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(directory))
                                Directory.CreateDirectory(directory);

                            if (!entry.FullName.EndsWith("/"))
                            {
                                entry.ExtractToFile(fullPath, true);
                            }
                        }
                        return extractDir;
                    }
                    catch { }

                    try { File.Delete(savePath); } catch { }
                }
            }

            try { Directory.Delete(extractDir, true); } catch { }
            return null;
        }

        private async Task<string> DownloadUnitPCAsync(string uid, string parentDir, int bookNumber)
        {
            var patterns = new[]
            {
                $"{parentDir}/unitUID_{uid}_resource_pc.zip",
                $"{parentDir}/unitUID_{uid}_pc.zip",
            };

            var extractDir = Path.Combine(Path.GetTempPath(), $"unit_{uid}_pc");

            foreach (var pattern in patterns)
            {
                var url = $"{BaseUrl}/{pattern}";
                var savePath = Path.Combine(Path.GetTempPath(), $"unit_{uid}_pc.zip");

                if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                {
                    try
                    {
                        Directory.CreateDirectory(extractDir);
                        using var zip = ZipFile.OpenRead(savePath);
                        foreach (var entry in zip.Entries)
                        {
                            var fullPath = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                            var directory = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(directory))
                                Directory.CreateDirectory(directory);

                            if (!entry.FullName.EndsWith("/"))
                            {
                                entry.ExtractToFile(fullPath, true);
                            }
                        }
                        return extractDir;
                    }
                    catch { }

                    try { File.Delete(savePath); } catch { }
                }
            }

            return null;
        }

        private void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(source))
            {
                var destFile = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
            }
        }
    }
}
