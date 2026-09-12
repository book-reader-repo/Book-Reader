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

        public void LoadFromXml(string xmlContent, string folderPath)
        {
            FolderPath = folderPath;
            var doc = new XmlDocument();
            doc.LoadXml(xmlContent);

            var bookNode = doc.SelectSingleNode("//book");
            if (bookNode != null)
            {
                BookId = bookNode.Attributes?["id"]?.Value ?? "";
                Title = bookNode.Attributes?["name"]?.Value ?? "";
                CoverPath = bookNode.Attributes?["coverpath"]?.Value ?? "";
            }

            // Units
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
                    Units.Add(unit);
                    UnitSections[unit.Id] = new List<SectionData>();
                }
            }

            // Sections
            var sectionDetailNodes = doc.SelectNodes("//sectiondetails/sectiondetail");
            if (sectionDetailNodes != null)
            {
                foreach (XmlNode node in sectionDetailNodes)
                {
                    var section = new SectionData
                    {
                        Id = node.Attributes?["id"]?.Value ?? "",
                        Name = node.Attributes?["name"]?.Value ?? "",
                        PageStart = node.Attributes?["page"]?.Value ?? ""
                    };
                    AllSections.Add(section);
                }
            }

            // Link sections to units
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
        public List<ResourceData> Resources { get; set; } = new();
        public List<string> StepFiles { get; set; } = new();
    }

    public class ResourceData
    {
        public string Type { get; set; } = "";       // "audio", "video", "doc", "link"
        public string Description { get; set; } = "";
        public string PageNumber { get; set; } = "";
        public string Path { get; set; } = "";       // resolved local or remote path
        public string Icon { get; set; } = "";
    }
}
