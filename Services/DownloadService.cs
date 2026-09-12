using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
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
            if (_isDownloading) { ... return; }
            _isDownloading = true;
        
            try
            {
                Log($"=== DOWNLOAD BOOK {bookNumber} START ===");
                OnProgress?.Invoke(this, 0);
        
                var booksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BookViewer", "Books");
                Directory.CreateDirectory(booksDir);
                Log($"Books directory: {booksDir}");
        
                var tempDir = Path.Combine(booksDir, "temp_" + Guid.NewGuid().ToString().Substring(0, 8));
                Directory.CreateDirectory(tempDir);
                Log($"Temp directory: {tempDir}");
        
                var finalBookDir = Path.Combine(booksDir, $"book_{bookNumber}");
                if (Directory.Exists(finalBookDir))
                {
                    Log($"Book {bookNumber} already exists at {finalBookDir}");
                    OnError?.Invoke(this, $"Book {bookNumber} already exists.");
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
        
                    Log($"Trying URL: {url}");
                    OnProgress?.Invoke(this, 10 + (i * 20));
        
                    if (await DownloadFileAsync(url, savePath))
                    {
                        Log($"Downloaded to: {savePath}");
                        if (await VerifyZipAsync(savePath))
                        {
                            Log($"Zip verified, extracting...");
                            bookExtractDir = Path.Combine(tempDir, "extracted");
                            Directory.CreateDirectory(bookExtractDir);
        
                            OnProgress?.Invoke(this, 30);
                            await ExtractZipAsync(savePath, bookExtractDir);
                            File.Delete(savePath);
                            bookDownloaded = true;
        
                            parentDir = pattern.Contains("/P02/")
                                ? pattern.Split("/P02/")[0] + "/P02"
                                : pattern.Substring(0, pattern.LastIndexOf('/'));
                            Log($"Parent dir: {parentDir}");
        
                            OnProgress?.Invoke(this, 50);
                            Log("Downloading resource PC zip...");
                            await DownloadAndExtractResourcePCAsync(bookNumber, parentDir, bookExtractDir);
        
                            OnProgress?.Invoke(this, 70);
                            var unitUids = FindUnitUids(bookExtractDir);
                            Log($"Found {unitUids.Count} unit UIDs: {string.Join(", ", unitUids)}");
        
                            if (unitUids.Any())
                            {
                                Log("Downloading unit zips...");
                                await DownloadAndExtractUnitsAsync(unitUids, parentDir, bookNumber, bookExtractDir);
                            }
        
                            OnProgress?.Invoke(this, 85);
                            Directory.CreateDirectory(finalBookDir);
                            CopyDirectory(bookExtractDir, finalBookDir);
                            Log($"Copied to final directory: {finalBookDir}");
        
                            await CopyBookCoverAsync(bookNumber, finalBookDir);
        
                            try { Directory.Delete(tempDir, true); } catch { }
        
                            OnProgress?.Invoke(this, 100);
                            Log($"=== DOWNLOAD BOOK {bookNumber} SUCCESS ===");
                            OnComplete?.Invoke(this, finalBookDir);
                            _isDownloading = false;
                            return;
                        }
                        else
                        {
                            Log($"Zip verification failed for {savePath}");
                            File.Delete(savePath);
                        }
                    }
                    else
                    {
                        Log($"Failed to download from {url}");
                    }
                }
        
                if (!bookDownloaded)
                {
                    Log($"=== DOWNLOAD BOOK {bookNumber} FAILED ===");
                    OnError?.Invoke(this, $"Failed to download book {bookNumber}");
                }
            }
            catch (Exception ex)
            {
                Log($"Exception: {ex.Message}");
                OnError?.Invoke(this, $"Download failed: {ex.Message}");
            }
            finally
            {
                _isDownloading = false;
            }
        }
        private async Task ExtractZipAsync(string zipPath, string extractDir)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/"))
                        continue;

                    // Normalize path: replace backslashes with forward slashes
                    var entryName = entry.FullName.Replace('\\', '/');
                    var fullPath = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    entry.ExtractToFile(fullPath, true);
                }
                Log($"Extracted zip to: {extractDir}");
            }
            catch (Exception ex)
            {
                Log($"Error extracting zip: {ex.Message}");
                throw;
            }
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
                var destSubDir = Path.Combine(dest, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private async Task DownloadAndExtractResourcePCAsync(int bookNumber, string parentDir, string extractDir)
        {
            try
            {
                var patterns = new[]
                {
                    $"{parentDir}/book_{bookNumber}_resource_pc.zip",
                    $"ebook_distribute/V3_otf/{bookNumber}/P02/book_{bookNumber}_resource_pc.zip",
                };

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
                                if (entry.FullName.EndsWith("/"))
                                    continue;

                                // Normalize path: replace backslashes with forward slashes
                                var entryName = entry.FullName.Replace('\\', '/');
                                var fullPath = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                                var directory = Path.GetDirectoryName(fullPath);
                                if (!string.IsNullOrEmpty(directory))
                                    Directory.CreateDirectory(directory);

                                if (!File.Exists(fullPath))
                                {
                                    entry.ExtractToFile(fullPath, true);
                                }
                            }
                            Log($"Extracted resource PC zip to: {extractDir}");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error extracting resource PC zip: {ex.Message}");
                        }

                        try { File.Delete(savePath); } catch { }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error downloading resource PC: {ex.Message}");
            }
        }

        private List<string> FindUnitUids(string extractDir)
        {
            var unitUids = new HashSet<string>();

            try
            {
                // Look for unit folders
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

                // Look in XML/JSON files
                foreach (var file in Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".xml") || file.EndsWith(".json") || file.EndsWith(".txt"))
                    {
                        try
                        {
                            var content = File.ReadAllText(file);
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
            catch (Exception ex)
            {
                Log($"Error finding unit UIDs: {ex.Message}");
                return new List<string>();
            }
        }

        private async Task DownloadAndExtractUnitsAsync(List<string> unitUids, string parentDir, int bookNumber, string extractDir)
        {
            try
            {
                foreach (var uid in unitUids)
                {
                    await DownloadAndExtractUnitAsync(uid, parentDir, bookNumber, extractDir);
                }
            }
            catch (Exception ex)
            {
                Log($"Error downloading units: {ex.Message}");
            }
        }

        private async Task DownloadAndExtractUnitAsync(string uid, string parentDir, int bookNumber, string extractDir)
        {
            try
            {
                var patterns = new[]
                {
                    $"{parentDir}/unitUID_{uid}.zip",
                    $"{parentDir}/unit_{uid}.zip",
                };

                foreach (var pattern in patterns)
                {
                    var url = $"{BaseUrl}/{pattern}";
                    var savePath = Path.Combine(Path.GetTempPath(), $"unit_{uid}.zip");

                    if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                    {
                        try
                        {
                            using var zip = ZipFile.OpenRead(savePath);
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.EndsWith("/"))
                                    continue;

                                // Normalize path: replace backslashes with forward slashes
                                var entryName = entry.FullName.Replace('\\', '/');
                                var fullPath = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                                var directory = Path.GetDirectoryName(fullPath);
                                if (!string.IsNullOrEmpty(directory))
                                    Directory.CreateDirectory(directory);

                                if (!File.Exists(fullPath))
                                {
                                    entry.ExtractToFile(fullPath, true);
                                }
                            }
                            Log($"Extracted unit {uid} to: {extractDir}");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error extracting unit {uid}: {ex.Message}");
                        }

                        try { File.Delete(savePath); } catch { }
                        break;
                    }
                }

                // Also try to download unit PC resource
                await DownloadAndExtractUnitPCAsync(uid, parentDir, bookNumber, extractDir);
            }
            catch (Exception ex)
            {
                Log($"Error downloading unit {uid}: {ex.Message}");
            }
        }

        private async Task DownloadAndExtractUnitPCAsync(string uid, string parentDir, int bookNumber, string extractDir)
        {
            try
            {
                var patterns = new[]
                {
                    $"{parentDir}/unitUID_{uid}_resource_pc.zip",
                    $"{parentDir}/unitUID_{uid}_pc.zip",
                };

                foreach (var pattern in patterns)
                {
                    var url = $"{BaseUrl}/{pattern}";
                    var savePath = Path.Combine(Path.GetTempPath(), $"unit_{uid}_pc.zip");

                    if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                    {
                        try
                        {
                            using var zip = ZipFile.OpenRead(savePath);
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.EndsWith("/"))
                                    continue;

                                // Normalize path: replace backslashes with forward slashes
                                var entryName = entry.FullName.Replace('\\', '/');
                                var fullPath = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                                var directory = Path.GetDirectoryName(fullPath);
                                if (!string.IsNullOrEmpty(directory))
                                    Directory.CreateDirectory(directory);

                                if (!File.Exists(fullPath))
                                {
                                    entry.ExtractToFile(fullPath, true);
                                }
                            }
                            Log($"Extracted unit PC {uid} to: {extractDir}");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error extracting unit PC {uid}: {ex.Message}");
                        }

                        try { File.Delete(savePath); } catch { }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error downloading unit PC {uid}: {ex.Message}");
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
                    string[] coverUrls = new[]
                    {
                        $"{BaseUrl}/ebook_distribute/V3_otf/{bookNumber}/P02/{bookNumber}.png",
                        $"{BaseUrl}/ebook_distribute/V3_otf/{bookNumber}/book_{bookNumber}.png",
                    };

                    string coverPath = Path.Combine(bookDir, $"{bookNumber}.png");
                    
                    foreach (var coverUrl in coverUrls)
                    {
                        if (await DownloadFileAsync(coverUrl, coverPath))
                        {
                            foundCover = coverPath;
                            Log($"Downloaded cover image for book {bookNumber}");
                            break;
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
    }
}
