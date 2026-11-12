using System;
using System.IO;
using UnityEngine;

namespace QuestDesktop
{
    [Serializable]
    public sealed class ConnectionConfig
    {
        public string lanHost = "192.168.1.10";
        public string remoteHost = "";
        public int port = 47990;
        public string token = "";
        public string certificateSha256 = "";
        public bool autoConnect;
    }

    [Serializable]
    public sealed class DisplayConfig
    {
        public bool hud;
        public float width = 1.6f;
        public float hudWidth = 0.9f;
        public float hudX;
        public float hudY = 0.05f;
        public float hudDistance = 1.2f;
        public float hudOpacity = 0.8f;
        public float brightness = 1f;
        public Vector3 worldPosition;
        public Vector3 worldEuler;
        public bool hasWorldPose;

        public void Clamp()
        {
            width = Mathf.Clamp(width, 0.25f, 6f);
            hudWidth = Mathf.Clamp(hudWidth, 0.2f, 2.5f);
            hudDistance = Mathf.Clamp(hudDistance, 0.5f, 3f);
            hudX = Mathf.Clamp(hudX, -2f, 2f);
            hudY = Mathf.Clamp(hudY, -1.5f, 1.5f);
            hudOpacity = Mathf.Clamp01(hudOpacity);
            brightness = Mathf.Clamp(brightness, 0.1f, 1f);
        }
    }

    public static class ConfigFiles
    {
        public static T Load<T>(string name) where T : new()
        {
            string path = Path.Combine(Application.persistentDataPath, name);
            if (!File.Exists(path)) return new T();
            return JsonUtility.FromJson<T>(File.ReadAllText(path)) ?? new T();
        }

        public static void Save<T>(string name, T config)
        {
            string path = Path.Combine(Application.persistentDataPath, name);
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(config, true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);
        }
    }
}
