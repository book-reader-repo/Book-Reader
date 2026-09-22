using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace BookViewer.Models
{
    public class BookData
    {
        public string BookId { get; set; } = "";
        public string Title { get; set; } = "";
        public string CoverPath { get; set; } = "";
        public string FolderPath { get; set; } = "";

        public List<UnitData> Units { get; set; } = new();
        public Dictionary<string, List<SectionData>> UnitSections { get; set; } = new();
        public List<SectionData> AllSections { get; set; } = new();


        // Parse sectiongroups → resources attached to sections
        // A <sectiongroup> has attributes: link/desc/path, and children <sectionset><section id="s_XXXX"/>
        var sectionGroups = doc.SelectNodes("//sectiongroup");
        if (sectionGroups != null)
        {
            foreach (XmlNode sg in sectionGroups)
            {
                var desc = sg.Attributes?["desc"]?.Value ?? "";
                var path = sg.Attributes?["path"]?.Value ?? "";
                var link = sg.Attributes?["link"]?.Value ?? "";
        
                // Prefer platform-specific, fall back to generic
                var url = link;
                if (string.IsNullOrEmpty(url)) url = sg.Attributes?["teacherios"]?.Value ?? "";
                if (string.IsNullOrEmpty(url)) url = sg.Attributes?["studentiospath"]?.Value ?? "";
                if (string.IsNullOrEmpty(url)) url = sg.Attributes?["teacherpc"]?.Value ?? "";
                if (string.IsNullOrEmpty(url)) url = sg.Attributes?["studentpc"]?.Value ?? "";
        
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(desc))
                    continue;
        
                // Determine type from extension
                var lower = url.ToLower();
                var type = "link";
                if (lower.Contains(".mp3") || lower.Contains(".m4a") || lower.Contains(".wav") ||
                    lower.Contains("/audio/") || lower.Contains("_rpa_"))
                    type = "audio";
                else if (lower.Contains(".mp4") || lower.Contains(".mov") || lower.Contains("video"))
                    type = "video";
                else if (lower.Contains(".pdf"))
                    type = "pdf";
                else if (lower.Contains(".docx") || lower.Contains(".doc") ||
                         lower.Contains(".pptx") || lower.Contains(".xlsx"))
                    type = "doc";
        
                var resource = new ResourceData
                {
                    Type = type,
                    Description = Uri.UnescapeDataString(desc),
                    Path = Uri.UnescapeDataString(url),
                    Icon = Uri.UnescapeDataString(path),
                    PageNumber = ""
                };
        
                // Attach to every section listed under this sectiongroup
                var sectionRefs = sg.SelectNodes(".//sectionset/section");
                if (sectionRefs != null)
                {
                    foreach (XmlNode sr in sectionRefs)
                    {
                        var sectionId = sr.Attributes?["id"]?.Value ?? "";
                        if (string.IsNullOrEmpty(sectionId)) continue;
        
                        var section = AllSections.FirstOrDefault(s => s.Id == sectionId);
                        if (section != null)
                        {
                            section.Resources.Add(resource);
        
                            // Pull page number out of the desc, e.g. "(第18頁)"
                            var m = System.Text.RegularExpressions.Regex.Match(desc, @"\(第(\d+)頁\)");
                            if (m.Success)
                                resource.PageNumber = m.Groups[1].Value;
                        }
                    }
                }
            }
        }

        
        /// <summary>folio → step file basename (e.g. 2 → "steps_1")</summary>
        public Dictionary<int, string> FolioToStepFile { get; set; } = new();

        public void LoadFromXml(string xmlContent, string folderPath)
        {
            FolderPath = folderPath;

            // book.xml may be a bare sequence of top-level nodes without a root
            // (unitdetails, sectiondetails, etc.). Wrap in <root> if needed.
            var trimmed = xmlContent.TrimStart();
            if (!trimmed.StartsWith("<book") && !trimmed.StartsWith("<?xml") && !trimmed.StartsWith("<root"))
            {
                xmlContent = $"<root>{xmlContent}</root>";
            }

            var doc = new XmlDocument();
            doc.LoadXml(xmlContent);

            // Book metadata
            var bookNode = doc.SelectSingleNode("//book");
            if (bookNode != null)
            {
                BookId = bookNode.Attributes?["id"]?.Value ?? "";
                Title = bookNode.Attributes?["name"]?.Value ?? "";
                CoverPath = bookNode.Attributes?["coverpath"]?.Value ?? "";
            }

            // Units (from <modules><module><unit>)
            var unitNodes = doc.SelectNodes("//unit");
            if (unitNodes != null)
            {
                foreach (XmlNode node in unitNodes)
                {
                    var unit = new UnitData
                    {
                        Id = node.Attributes?["id"]?.Value ?? "",
                        Title = node.Attributes?["name"]?.Value ?? ""
                    };

                    // Avoid duplicates (some books list units in multiple places)
                    if (!Units.Any(u => u.Id == unit.Id))
                    {
                        Units.Add(unit);
                        UnitSections[unit.Id] = new List<SectionData>();
                    }
                }
            }

            // Sections + their nested <page> entries
            var sectionDetailNodes = doc.SelectNodes("//sectiondetails/sectiondetail");
            if (sectionDetailNodes != null)
            {
                foreach (XmlNode node in sectionDetailNodes)
                {
                    var section = new SectionData
                    {
                        Id = node.Attributes?["id"]?.Value ?? "",
                        Name = node.Attributes?["name"]?.Value ?? "",
                        PageStart = node.Attributes?["pagestart"]?.Value ?? ""
                    };

                    // Parse nested <page file="steps_1" folio="2" seq="1" .../>
                    var pageNodes = node.SelectNodes(".//page");
                    if (pageNodes != null)
                    {
                        foreach (XmlNode pageNode in pageNodes)
                        {
                            var file = pageNode.Attributes?["file"]?.Value ?? "";
                            var folioStr = pageNode.Attributes?["folio"]?.Value ?? "";

                            if (!string.IsNullOrEmpty(file))
                                section.StepFiles.Add(file);   // "steps_1"

                            if (int.TryParse(folioStr, out int folio) && !string.IsNullOrEmpty(file))
                            {
                                if (!FolioToStepFile.ContainsKey(folio))
                                    FolioToStepFile[folio] = file;
                            }
                        }
                    }

                    AllSections.Add(section);
                }
            }

            // Link sections to units via <unitdetail><sectiongroup><sectionset><section id="..."/>
            var unitDetailNodes = doc.SelectNodes("//unitdetail");
            if (unitDetailNodes != null)
            {
                foreach (XmlNode node in unitDetailNodes)
                {
                    string unitId = node.Attributes?["id"]?.Value ?? "";
                    if (!UnitSections.ContainsKey(unitId)) continue;

                    var sectionNodes = node.SelectNodes(".//section");
                    if (sectionNodes != null)
                    {
                        foreach (XmlNode sectionNode in sectionNodes)
                        {
                            string sectionId = sectionNode.Attributes?["id"]?.Value ?? "";
                            var section = AllSections.FirstOrDefault(s => s.Id == sectionId);
                            if (section != null && !UnitSections[unitId].Contains(section))
                                UnitSections[unitId].Add(section);
                        }
                    }
                }
            }
        }

        public List<SectionData> GetSectionsForUnit(string unitId)
            => UnitSections.TryGetValue(unitId, out var s) ? s : new List<SectionData>();
    }

    public class UnitData
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    public class SectionData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string PageStart { get; set; } = "";
        public List<string> StepFiles { get; set; } = new();   // from nested <page file="..."/>
        public List<ResourceData> Resources { get; set; } = new();
    }

    public class ResourceData
    {
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string PageNumber { get; set; } = "";
        public string Path { get; set; } = "";
        public string Icon { get; set; } = "";
    }
}
