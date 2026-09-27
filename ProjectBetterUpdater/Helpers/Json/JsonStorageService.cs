using System;
using System.IO;
using System.Text.Json;

namespace ProjectBetterUpdater.Helpers.Json;

public static class JsonStorageService
{
    private static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;

    public static void Save<T>(string fileName, T data)
    {
        try
        {
            string filePath = Path.Combine(BaseDirectory, fileName);
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(data, options);
            File.WriteAllText(filePath, jsonString);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
    
    public static T Load<T>(string fileName, T defaultValue)
    {
        try
        {
            string filePath = Path.Combine(BaseDirectory, fileName);
            
            if (!File.Exists(filePath))
            {
                Save(fileName, defaultValue);
                return defaultValue;
            }

            string jsonString = File.ReadAllText(filePath);
            var result = JsonSerializer.Deserialize<T>(jsonString);
            
            return result != null ? result : defaultValue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JSON] Couldn't read file: {fileName}: {ex.Message}");
            return defaultValue;
        }
    }
}