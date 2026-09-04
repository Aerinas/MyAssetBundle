using System.IO;
using System.Xml;
using System.Xml.Serialization;
using UnityEngine;

namespace AssetBundleFrameWork.Editor
{
    public class XMLTool
    {
        public static T ReadXml<T>(string filepath)
        {
            
            if (!File.Exists(filepath))
            {
                Debug.LogError($"File {filepath} does not exist");
                return default(T);
            }
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(T));
                using (FileStream stream = new FileStream(filepath, FileMode.Open))
                {
                    return (T)serializer.Deserialize(stream);
                }
            }
            catch
            {
                Debug.LogError($"Failed to read XML file: {filepath}");
                return default(T);
            }
        }
    }
}