namespace PvfLib.IO;

// 共享目录前缀，完整路径只在枚举时重建；查询直接比较规范化路径的两段切片。
internal readonly record struct PvfPathKey(string Directory, string Name)
{
    internal string Path => Directory.Length == 0 ? Name : Directory + "/" + Name;

    internal sealed class Comparer : IEqualityComparer<PvfPathKey>, IAlternateEqualityComparer<ReadOnlySpan<char>, PvfPathKey>
    {
        public bool Equals(PvfPathKey left, PvfPathKey right) => left == right;
        public int GetHashCode(PvfPathKey key) => HashCode.Combine(
            string.GetHashCode(key.Directory, StringComparison.Ordinal), string.GetHashCode(key.Name, StringComparison.Ordinal));

        public bool Equals(ReadOnlySpan<char> path, PvfPathKey key)
        {
            int slash = path.LastIndexOf('/');
            return Parent(path, slash).SequenceEqual(key.Directory) && path[(slash + 1)..].SequenceEqual(key.Name);
        }

        public int GetHashCode(ReadOnlySpan<char> path)
        {
            int slash = path.LastIndexOf('/');
            return HashCode.Combine(string.GetHashCode(Parent(path, slash), StringComparison.Ordinal),
                string.GetHashCode(path[(slash + 1)..], StringComparison.Ordinal));
        }

        public PvfPathKey Create(ReadOnlySpan<char> path)
        {
            int slash = path.LastIndexOf('/');
            return new(Parent(path, slash).ToString(), path[(slash + 1)..].ToString());
        }

        private static ReadOnlySpan<char> Parent(ReadOnlySpan<char> path, int slash) => slash < 0 ? [] : path[..slash];
    }
}
