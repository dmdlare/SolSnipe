using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SolSnipe.Data;

public class SolSnipeDb : IDisposable
{
    private readonly ILogger<SolSnipeDb> _logger;
    private readonly string _path;
    private LiteDatabase _db;

    public SolSnipeDb(IConfiguration config, ILogger<SolSnipeDb> logger)
    {
        _logger = logger;

        var configPath = config["Database:Path"] ?? "data/solsnipe.db";

        
        if (Path.IsPathRooted(configPath))
        {
            _path = configPath;
        }
        else
        {
            
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < 4; i++)
                dir = Path.GetDirectoryName(dir) ?? dir;

            _path = Path.Combine(dir, configPath);
        }

        _path = _path.Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

        _logger.LogInformation("Database path: {Path}", _path);
        _db = OpenDatabase();
    }

    public ILiteCollection<T> GetCollection<T>(string name) =>
        _db.GetCollection<T>(name);

    public void Dispose() => _db.Dispose();

    private LiteDatabase OpenDatabase()
    {
        try
        {
            var db = new LiteDatabase(_path);
            db.GetCollectionNames(); // sanity check
            _logger.LogInformation("Database opened successfully");
            return db;
        }
        catch (LiteException ex)
        {
            _logger.LogWarning("Database corrupt ({Err}) - recreating", ex.Message);
            DeleteDatabaseFiles();
            return new LiteDatabase(_path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Database open failed ({Err}) - recreating", ex.Message);
            DeleteDatabaseFiles();
            return new LiteDatabase(_path);
        }
    }

    private void DeleteDatabaseFiles()
    {
        var logPath = Path.ChangeExtension(_path, null) + "-log.db";
        foreach (var file in new[] { _path, logPath })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { }
        }
    }
}