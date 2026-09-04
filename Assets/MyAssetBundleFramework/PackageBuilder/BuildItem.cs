using System.Collections.Generic;
using System.Xml.Serialization;

namespace AssetBundleFrameWork.Editor
{
    public class BuildItem
    {
        [XmlAttribute("BundleType")]
        public EBundleType bundleType { get; set; } = EBundleType.File;

        [XmlAttribute("AssetPath")]
        public string assetPath { get; set; } = string.Empty;

        [XmlAttribute("ResourceType")]
        public EResourceType resourceType { get; set; } = EResourceType.Direct;

        [XmlAttribute("Suffix")]
        public string suffix { get; set; } = ".prefab";

        public List<string> ignorePaths { get; set; } = new();

        public List<string> suffixes { get; set; } = new();

        public int count { get; set; } = 0;
        

    }
}