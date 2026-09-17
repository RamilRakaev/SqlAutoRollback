namespace SqlAutoRollback.Services;

public interface IScriptFileService
{
    string Read(string path);

    void Save(string path, string contents);

    string SaveNew(string contents);
}
