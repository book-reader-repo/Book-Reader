using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BookViewer
{
    public class DecryptionService
    {
        private const int DEFAULT_OFFSET = 99999;
        private const string ENCRYPT_MAP = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz";
        private readonly Dictionary<int, string> _decryptMapCache = new();

        public bool IsEncrypted(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;

            // If it contains HTML/XML tags, it's likely already decrypted
            if (content.Contains("<") && content.Contains(">") &&
                (content.Contains("</") || content.Contains("/>")))
                return false;

            // Check if content looks like encrypted data (mostly alphanumeric)
            int total = Math.Min(500, content.Length);
            int valid = 0;
            foreach (char c in content.Take(total))
            {
                if (ENCRYPT_MAP.Contains(c))
                    valid++;
            }

            double ratio = (double)valid / total;
            return ratio > 0.7;
        }

        // ============================================================
        // DECRYPT HTML - ALWAYS decrypted, uses key from filename number
        // ============================================================
        public string DecryptHtml(string content, string fileName)
        {
            // Extract number from filename (e.g., "steps_1.html" -> 1)
            var match = Regex.Match(fileName, @"(\d+)");
            int key;
            if (match.Success && int.TryParse(match.Value, out int num))
            {
                key = num + DEFAULT_OFFSET;
            }
            else
            {
                key = DEFAULT_OFFSET;
            }
            
            return DecryptWithKey(content, key);
        }

        // ============================================================
        // DECRYPT XML/HTM - Check if encrypted, use file-specific key
        // ============================================================
        public string DecryptXmlOrHtm(string content, string fileName)
        {
            // First check if it's encrypted
            if (!IsEncrypted(content))
            {
                return content;
            }

            int key = GetKeyFromFileName(fileName);
            return DecryptWithKey(content, key);
        }

        // ============================================================
        // DECRYPT BOOK.XML - Uses book UID
        // ============================================================
        public string DecryptBookFile(string content, string bookUid)
        {
            int key = CalculateKey(bookUid);
            return DecryptWithKey(content, key);
        }

        // ============================================================
        // Get key from filename - EXACT match with console decryptor
        // ============================================================
        private int GetKeyFromFileName(string fileName)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            // EXACT from console: int.Parse(fileName.Split("_")[0]) + offset
            if (nameWithoutExt.Contains("_"))
            {
                string[] parts = nameWithoutExt.Split('_');
                if (parts.Length > 0 && int.TryParse(parts[0], out int keyNum))
                {
                    return keyNum + DEFAULT_OFFSET;
                }

                foreach (string part in parts)
                {
                    if (int.TryParse(part, out int num))
                    {
                        return num + DEFAULT_OFFSET;
                    }
                }
            }

            // For book.xml, use default
            if (fileName.Equals("book.xml", StringComparison.OrdinalIgnoreCase))
            {
                return DEFAULT_OFFSET;
            }

            // Try to find any number in the filename
            string digits = "";
            foreach (char c in nameWithoutExt)
            {
                if (char.IsDigit(c))
                    digits += c;
                else if (digits.Length > 0)
                    break;
            }
            if (int.TryParse(digits, out int num2))
            {
                return num2 + DEFAULT_OFFSET;
            }

            return DEFAULT_OFFSET;
        }

        // ============================================================
        // Calculate key from book ID
        // ============================================================
        public int CalculateKey(string bookUid)
        {
            if (string.IsNullOrEmpty(bookUid)) return DEFAULT_OFFSET;
            
            var match = Regex.Match(bookUid, @"\d+");
            if (match.Success && int.TryParse(match.Value, out int num))
                return num + DEFAULT_OFFSET;
            
            return DEFAULT_OFFSET;
        }

        // ============================================================
        // Decrypt with key - CORE METHOD
        // ============================================================
        public string DecryptWithKey(string input, int key)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Generate decrypt map for this key
            if (!_decryptMapCache.TryGetValue(key, out var decryptMap))
            {
                char[] result = new char[ENCRYPT_MAP.Length];
                List<int> indices = Enumerable.Range(0, ENCRYPT_MAP.Length).ToList();

                for (int i = 0; i < ENCRYPT_MAP.Length; i++)
                {
                    int index = key % indices.Count;
                    int position = indices[index];
                    indices.RemoveAt(index);
                    result[position] = ENCRYPT_MAP[i];
                }

                decryptMap = new string(result);
                _decryptMapCache[key] = decryptMap;
            }

            // Decrypt character by character
            char[] output = new char[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                int idx = decryptMap.IndexOf(c);
                output[i] = idx >= 0 ? ENCRYPT_MAP[idx] : c;
            }

            string decrypted = new string(output);

            // Try Base64 decode (same as console decryptor)
            try
            {
                byte[] bytes = Convert.FromBase64String(decrypted);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return decrypted;
            }
        }

        // ============================================================
        // Check if file needs decryption
        // ============================================================
        public bool ShouldDecrypt(string content, string fileName)
        {
            // HTML files: ALWAYS decrypt (NO encryption check)
            if (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                return true;

            // XML/HTM files: check if encrypted
            if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                return IsEncrypted(content);
            }

            return IsEncrypted(content);
        }

        // ============================================================
        // Legacy method for backward compatibility
        // ============================================================
        public string Decrypt(string content, string bookUid)
        {
            return DecryptBookFile(content, bookUid);
        }

        public string DecryptWithFileName(string content, string fileName)
        {
            if (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                return DecryptHtml(content, fileName);
            }
            else if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                return DecryptXmlOrHtm(content, fileName);
            }
            else
            {
                if (!IsEncrypted(content))
                    return content;
                int key = GetKeyFromFileName(fileName);
                return DecryptWithKey(content, key);
            }
        }
    }
}
