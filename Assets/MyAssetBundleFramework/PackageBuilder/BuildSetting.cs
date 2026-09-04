using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Xml.Serialization;
using FileMode = System.IO.FileMode;

namespace AssetBundleFrameWork.Editor
{
    public class BuildSetting
    {
        [DisplayName("项目名称")]
        [XmlAttribute("ProjectName")]
        public string projectName { get; set; } = string.Empty;

        [DisplayName("后缀列表")]
        [XmlAttribute("SuffixList")]
        public string suffixList { get; set; } = string.Empty;

        [DisplayName("打包文件的目标文件夹")]
        [XmlAttribute("BuildRoot")]
        public string buildRoot { get; set; } = string.Empty;

        [DisplayName("打包文件列表")]
        [XmlElement("BuildItem")]
        public List<BuildItem> buildItems { get; set; } = new();

        [XmlIgnore]
        public Dictionary<string, BuildItem> itemDict = new();
        
        private void EndInit()
        {
            // 规范化输出目录
            buildItems ??= new List<BuildItem>();
            itemDict.Clear();
            
            foreach (var item in buildItems)
            {
                if (item == null || string.IsNullOrEmpty(item.assetPath))
                {
                    throw new System.Exception("BuildItem or its assetPath is null or empty.");
                }
                string assetPath = item.assetPath.Replace("\\", "/");

                if (itemDict.ContainsKey(assetPath))
                {
                    throw new System.Exception($"Duplicate asset path rule: {assetPath}");
                }
                
                if(!itemDict.TryAdd(assetPath, item))
                {
                    throw new System.Exception($"Failed to add asset path rule: {assetPath}");
                }
            }
        }
        
        public static BuildSetting LoadFromXml(string filepath)
        {
            if (!File.Exists(filepath))
            {
                throw new FileNotFoundException($"The specified XML file does not exist: {filepath}");
            }

            BuildSetting setting =  XMLTool.ReadXml<BuildSetting>(filepath);
            
            if (setting == null)
            {
                throw new InvalidDataException($"Failed to load BuildSetting from XML file: {filepath}");
            }

            setting.EndInit();
            return setting;
        }
    }
}