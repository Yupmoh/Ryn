namespace Ryn.Plugins.FileSystem;

public sealed class FileSystemOptions
{
    public List<string> AllowedPaths { get; set; } = [];

    /// <summary>
    /// Maximum number of bytes a single <c>fs.readFile</c>/<c>fs.readTextFile</c> may buffer.
    /// Guards against a hostile or runaway in-scope target (including a <c>/dev/zero</c>-style file
    /// reached through a symlink) exhausting process memory. Default 64 MiB. Set to 0 to disable.
    /// </summary>
    public long MaxReadBytes { get; set; } = 64L * 1024 * 1024;

    internal bool AccessDenied { get; set; }
}
