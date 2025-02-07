using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.HLE.Loaders.Mods;
using Ryujinx.HLE.Loaders.Processes;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using Path = System.IO.Path;

namespace Ryujinx.HLE.HOS
{
    public class ModLoader
    {
        private const string RomfsDir = "romfs";
        private const string ExefsDir = "exefs";
        private const string CheatDir = "cheats";
        private const string RomfsContainer = "romfs.bin";
        private const string ExefsContainer = "exefs.nsp";
        private const string StubExtension = ".stub";
        private const string CheatExtension = ".txt";
        private const string DefaultCheatName = "<default>";

        private const string AmsContentsDir = "contents";
        private const string AmsNsoPatchDir = "exefs_patches";
        private const string AmsNroPatchDir = "nro_patches";
        private const string AmsKipPatchDir = "kip_patches";

        public readonly struct Mod<T> where T : FileSystemInfo
        {
            public readonly string Name;
            public readonly T Path;

            public Mod(string name, T path)
            {
                Name = name;
                Path = path;
            }
        }

        public struct Cheat
        {
            // Atmosphere identifies the executables with the first 8 bytes
            // of the build id, which is equivalent to 16 hex digits.
            public const int CheatIdSize = 16;

            public readonly string Name;
            public readonly FileInfo Path;
            public readonly IEnumerable<String> Instructions;

            public Cheat(string name, FileInfo path, IEnumerable<String> instructions)
            {
                Name = name;
                Path = path;
                Instructions = instructions;
            }
        }

        // Title dependent mods
        public class ModCache
        {
            public List<Mod<FileInfo>> RomfsContainers { get; }
            public List<Mod<FileInfo>> ExefsContainers { get; }

            public List<Mod<DirectoryInfo>> RomfsDirs { get; }
            public List<Mod<DirectoryInfo>> ExefsDirs { get; }

            public List<Cheat> Cheats { get; }

            public ModCache()
            {
                RomfsContainers = new List<Mod<FileInfo>>();
                ExefsContainers = new List<Mod<FileInfo>>();
                RomfsDirs = new List<Mod<DirectoryInfo>>();
                ExefsDirs = new List<Mod<DirectoryInfo>>();
                Cheats = new List<Cheat>();
            }
        }

        // Title independent mods
        private class PatchCache
        {
            public List<Mod<DirectoryInfo>> NsoPatches { get; }
            public List<Mod<DirectoryInfo>> NroPatches { get; }
            public List<Mod<DirectoryInfo>> KipPatches { get; }

            internal bool Initialized { get; set; }

            public PatchCache()
            {
                NsoPatches = new List<Mod<DirectoryInfo>>();
                NroPatches = new List<Mod<DirectoryInfo>>();
                KipPatches = new List<Mod<DirectoryInfo>>();

                Initialized = false;
            }
        }

        private readonly Dictionary<ulong, ModCache> _appMods; // key is TitleId
        private PatchCache _patches;

        private static readonly EnumerationOptions _dirEnumOptions;

        static ModLoader()
        {
            _dirEnumOptions = new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Simple,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
        }

        public ModLoader()
        {
            _appMods = new Dictionary<ulong, ModCache>();
            _patches = new PatchCache();
        }

        private void Clear()
        {
            _appMods.Clear();
            _patches = new PatchCache();
        }

        private static bool StrEquals(string s1, string s2) => string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase);

        public static string GetModsBasePath() => EnsureBaseDirStructure(AppDataManager.GetModsPath());
        public static string GetSdModsBasePath() => EnsureBaseDirStructure(AppDataManager.GetSdModsPath());

        private static string EnsureBaseDirStructure(string modsBasePath)
        {
            var modsDir = new DirectoryInfo(modsBasePath);

            modsDir.CreateSubdirectory(AmsContentsDir);
            modsDir.CreateSubdirectory(AmsNsoPatchDir);
            modsDir.CreateSubdirectory(AmsNroPatchDir);
            // TODO: uncomment when KIPs are supported
            // modsDir.CreateSubdirectory(AmsKipPatchDir);

            return modsDir.FullName;
        }

        private static DirectoryInfo FindTitleDir(DirectoryInfo contentsDir, string titleId)
            => contentsDir.EnumerateDirectories(titleId, _dirEnumOptions).FirstOrDefault();

        private static void AddModsFromDirectory(ModCache mods, DirectoryInfo dir, string titleId)
        {
            System.Text.StringBuilder types = new();

            foreach (var modDir in dir.EnumerateDirectories())
            {
                types.Clear();
                Mod<DirectoryInfo> mod = new("", null);

                if (StrEquals(RomfsDir, modDir.Name))
                {
                    mods.RomfsDirs.Add(mod = new Mod<DirectoryInfo>(dir.Name, modDir));
                    types.Append('R');
                }
                else if (StrEquals(ExefsDir, modDir.Name))
                {
                    mods.ExefsDirs.Add(mod = new Mod<DirectoryInfo>(dir.Name, modDir));
                    types.Append('E');
                }
                else if (StrEquals(CheatDir, modDir.Name))
                {
                    types.Append('C', QueryCheatsDir(mods, modDir));
                }
                else
                {
                    AddModsFromDirectory(mods, modDir, titleId);
                }

                if (types.Length > 0)
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"Found mod '{mod.Name}' [{types}]");
                }
            }
        }

        public static string GetTitleDir(string modsBasePath, string titleId)
        {
            var contentsDir = new DirectoryInfo(Path.Combine(modsBasePath, AmsContentsDir));
            var titleModsPath = FindTitleDir(contentsDir, titleId);

            if (titleModsPath == null)
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Creating mods directory for Title {titleId.ToUpper()}");
                titleModsPath = contentsDir.CreateSubdirectory(titleId);
            }

            return titleModsPath.FullName;
        }

        // Static Query Methods
        private static void QueryPatchDirs(PatchCache cache, DirectoryInfo patchDir)
        {
            if (cache.Initialized || !patchDir.Exists)
            {
                return;
            }

            List<Mod<DirectoryInfo>> patches;
            string type;

            if (StrEquals(AmsNsoPatchDir, patchDir.Name))
            {
                patches = cache.NsoPatches;
                type = "NSO";
            }
            else if (StrEquals(AmsNroPatchDir, patchDir.Name))
            {
                patches = cache.NroPatches;
                type = "NRO";
            }
            else if (StrEquals(AmsKipPatchDir, patchDir.Name))
            {
                patches = cache.KipPatches;
                type = "KIP";
            }
            else
            {
                return;
            }

            foreach (var modDir in patchDir.EnumerateDirectories())
            {
                patches.Add(new Mod<DirectoryInfo>(modDir.Name, modDir));
                Logger.Info?.Print(LogClass.ModLoader, $"Found {type} patch '{modDir.Name}'");
            }
        }

        private static void QueryTitleDir(ModCache mods, DirectoryInfo titleDir)
        {
            if (!titleDir.Exists)
            {
                return;
            }

            var fsFile = new FileInfo(Path.Combine(titleDir.FullName, RomfsContainer));
            if (fsFile.Exists)
            {
                mods.RomfsContainers.Add(new Mod<FileInfo>($"<{titleDir.Name} RomFs>", fsFile));
            }

            fsFile = new FileInfo(Path.Combine(titleDir.FullName, ExefsContainer));
            if (fsFile.Exists)
            {
                mods.ExefsContainers.Add(new Mod<FileInfo>($"<{titleDir.Name} ExeFs>", fsFile));
            }

            AddModsFromDirectory(mods, titleDir, titleDir.Name);
        }

        public static void QueryContentsDir(ModCache mods, DirectoryInfo contentsDir, ulong titleId)
        {
            if (!contentsDir.Exists)
            {
                return;
            }

            Logger.Info?.Print(LogClass.ModLoader, $"Searching mods for {((titleId & 0x1000) != 0 ? "DLC" : "Title")} {titleId:X16}");

            var titleDir = FindTitleDir(contentsDir, $"{titleId:x16}");

            if (titleDir != null)
            {
                QueryTitleDir(mods, titleDir);
            }
        }

        private static int QueryCheatsDir(ModCache mods, DirectoryInfo cheatsDir)
        {
            if (!cheatsDir.Exists)
            {
                return 0;
            }

            int numMods = 0;

            foreach (FileInfo file in cheatsDir.EnumerateFiles())
            {
                if (!StrEquals(CheatExtension, file.Extension))
                {
                    continue;
                }

                string cheatId = Path.GetFileNameWithoutExtension(file.Name);

                if (cheatId.Length != Cheat.CheatIdSize)
                {
                    continue;
                }

                if (!ulong.TryParse(cheatId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                int oldCheatsCount = mods.Cheats.Count;

                // A cheat file can contain several cheats for the same executable, so the file must be parsed in
                // order to properly enumerate them.
                mods.Cheats.AddRange(GetCheatsInFile(file));

                if (mods.Cheats.Count - oldCheatsCount > 0)
                {
                    numMods++;
                }
            }

            return numMods;
        }

        private static IEnumerable<Cheat> GetCheatsInFile(FileInfo cheatFile)
        {
            string cheatName = DefaultCheatName;
            List<string> instructions = new();
            List<Cheat> cheats = new();

            using StreamReader cheatData = cheatFile.OpenText();
            while (cheatData.ReadLine() is { } line)
            {
                line = line.Trim();

                if (line.StartsWith('['))
                {
                    // This line starts a new cheat section.
                    if (!line.EndsWith(']') || line.Length < 3)
                    {
                        // Skip the entire file if there's any error while parsing the cheat file.

                        Logger.Warning?.Print(LogClass.ModLoader, $"Ignoring cheat '{cheatFile.FullName}' because it is malformed");

                        return Array.Empty<Cheat>();
                    }

                    // Add the previous section to the list.
                    if (instructions.Count > 0)
                    {
                        cheats.Add(new Cheat($"<{cheatName} Cheat>", cheatFile, instructions));
                    }

                    // Start a new cheat section.
                    cheatName = line[1..^1];
                    instructions = new List<string>();
                }
                else if (line.Length > 0)
                {
                    // The line contains an instruction.
                    instructions.Add(line);
                }
            }

            // Add the last section being processed.
            if (instructions.Count > 0)
            {
                cheats.Add(new Cheat($"<{cheatName} Cheat>", cheatFile, instructions));
            }

            return cheats;
        }

        // Assumes searchDirPaths don't overlap
        private static void CollectMods(Dictionary<ulong, ModCache> modCaches, PatchCache patches, params string[] searchDirPaths)
        {
            static bool IsPatchesDir(string name) => StrEquals(AmsNsoPatchDir, name) ||
                                                     StrEquals(AmsNroPatchDir, name) ||
                                                     StrEquals(AmsKipPatchDir, name);

            static bool IsContentsDir(string name) => StrEquals(AmsContentsDir, name);

            static bool TryQuery(DirectoryInfo searchDir, PatchCache patches, Dictionary<ulong, ModCache> modCaches)
            {
                if (IsContentsDir(searchDir.Name))
                {
                    foreach ((ulong titleId, ModCache cache) in modCaches)
                    {
                        QueryContentsDir(cache, searchDir, titleId);
                    }

                    return true;
                }
                else if (IsPatchesDir(searchDir.Name))
                {
                    QueryPatchDirs(patches, searchDir);

                    return true;
                }

                return false;
            }

            foreach (var path in searchDirPaths)
            {
                var searchDir = new DirectoryInfo(path);
                if (!searchDir.Exists)
                {
                    Logger.Warning?.Print(LogClass.ModLoader, $"Mod Search Dir '{searchDir.FullName}' doesn't exist");
                    continue;
                }

                if (!TryQuery(searchDir, patches, modCaches))
                {
                    foreach (var subdir in searchDir.EnumerateDirectories())
                    {
                        TryQuery(subdir, patches, modCaches);
                    }
                }
            }

            patches.Initialized = true;
        }

        public void CollectMods(IEnumerable<ulong> titles, params string[] searchDirPaths)
        {
            Clear();

            foreach (ulong titleId in titles)
            {
                _appMods[titleId] = new ModCache();
            }

            CollectMods(_appMods, _patches, searchDirPaths);
        }

        internal IStorage ApplyRomFsMods(ulong titleId, IStorage baseStorage)
        {
            if (!_appMods.TryGetValue(titleId, out ModCache mods) || mods.RomfsDirs.Count + mods.RomfsContainers.Count == 0)
            {
                return baseStorage;
            }

            var fileSet = new HashSet<string>();
            var builder = new RomFsBuilder();
            int count = 0;

            Logger.Info?.Print(LogClass.ModLoader, $"Applying RomFS mods for Title {titleId:X16}");

            // Prioritize loose files first
            foreach (var mod in mods.RomfsDirs)
            {
                using (IFileSystem fs = new LocalFileSystem(mod.Path.FullName))
                {
                    AddLooseFiles(fs, mod.Name, mod.Path.FullName, fileSet, builder);
                }
                count++;
            }

            // Then files inside images
            foreach (var mod in mods.RomfsContainers)
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Found 'romfs.bin' for Title {titleId:X16}");
                using (IFileSystem fs = new RomFsFileSystem(mod.Path.OpenRead().AsStorage()))
                {
                    AddFiles(fs, mod.Name, fileSet, builder);
                }
                count++;
            }

            if (fileSet.Count == 0)
            {
                Logger.Info?.Print(LogClass.ModLoader, "No files found. Using base RomFS");

                return baseStorage;
            }

            Logger.Info?.Print(LogClass.ModLoader, $"Replaced {fileSet.Count} file(s) over {count} mod(s). Processing base storage...");

            // And finally, the base romfs
            var baseRom = new RomFsFileSystem(baseStorage);
            foreach (var entry in baseRom.EnumerateEntries()
                                         .Where(f => f.Type == DirectoryEntryType.File && !fileSet.Contains(f.FullPath))
                                         .OrderBy(f => f.FullPath, StringComparer.Ordinal))
            {
                using var file = new UniqueRef<IFile>();

                baseRom.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                builder.AddFile(entry.FullPath, file.Release());
            }

            Logger.Info?.Print(LogClass.ModLoader, "Building new RomFS...");
            IStorage newStorage = builder.Build();
            Logger.Info?.Print(LogClass.ModLoader, "Using modded RomFS");

            return newStorage;
        }

        private static void AddFiles(IFileSystem fs, string modName, ISet<string> fileSet, RomFsBuilder builder)
        {
            foreach (var entry in fs.EnumerateEntries()
                                    .Where(f => f.Type == DirectoryEntryType.File)
                                    .OrderBy(f => f.FullPath, StringComparer.Ordinal))
            {
                using var file = new UniqueRef<IFile>();

                fs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                if (fileSet.Add(entry.FullPath))
                {
                    builder.AddFile(entry.FullPath, file.Release());
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ModLoader, $"    Skipped duplicate file '{entry.FullPath}' from '{modName}'", "ApplyRomFsMods");
                }
            }
        }

        private static void AddLooseFiles(IFileSystem fs, string modName, string rootPath, ISet<string> fileSet, RomFsBuilder builder)
        {
            foreach (var entry in fs.EnumerateEntries()
                                    .AsParallel()
                                    .Where(f => f.Type == DirectoryEntryType.File)
                                    .OrderBy(f => f.FullPath, StringComparer.Ordinal))
            {
                var file = new LazyFsFile(entry.FullPath, rootPath, fs);
                if (fileSet.Add(entry.FullPath))
                {
                    builder.AddFile(entry.FullPath, file);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ModLoader, $"    Skipped duplicate file '{entry.FullPath}' from '{modName}'", "ApplyRomFsMods");
                }
            }
        }


        private class LazyFsFile : IFile
        {
            private readonly IFileSystem _fs;
            private readonly string _filePath;
            private readonly UniqueRef<IFile> _fileReference = new();
            private readonly FileInfo _fileInfo;

            public LazyFsFile(string filePath, string prefix, IFileSystem fs)
            {
                _fs = fs;
                _filePath = filePath;
                _fileInfo = new FileInfo(prefix + "/" + filePath);
            }

            private void PrepareFile()
            {
                if (_fileReference.Get == null)
                {
                    _fs.OpenFile(ref _fileReference.Ref, _filePath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                }
            }

            protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination,
                in ReadOption option)
            {
                PrepareFile();
                return _fileReference.Get!.Read(out bytesRead, offset, destination);
            }

            protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option)
            {
                throw new NotImplementedException();
            }

            protected override Result DoFlush()
            {
                throw new NotImplementedException();
            }

            protected override Result DoSetSize(long size)
            {
                throw new NotImplementedException();
            }

            protected override Result DoGetSize(out long size)
            {
                size = _fileInfo.Length;
                return new Result(0);
            }

            protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
            {
                throw new NotImplementedException();
            }
        }

        internal bool ReplaceExefsPartition(ulong titleId, ref IFileSystem exefs)
        {
            if (!_appMods.TryGetValue(titleId, out ModCache mods) || mods.ExefsContainers.Count == 0)
            {
                return false;
            }

            if (mods.ExefsContainers.Count > 1)
            {
                Logger.Warning?.Print(LogClass.ModLoader, "Multiple ExeFS partition replacements detected");
            }

            Logger.Info?.Print(LogClass.ModLoader, "Using replacement ExeFS partition");

            exefs = new PartitionFileSystem(mods.ExefsContainers[0].Path.OpenRead().AsStorage());

            return true;
        }

        public struct ModLoadResult
        {
            public BitVector32 Stubs;
            public BitVector32 Replaces;
            public MetaLoader Npdm;

            public bool Modified => (Stubs.Data | Replaces.Data) != 0;
        }

        internal ModLoadResult ApplyExefsMods(ulong titleId, NsoExecutable[] nsos)
        {
            ModLoadResult modLoadResult = new()
            {
                Stubs = new BitVector32(),
                Replaces = new BitVector32(),
            };

            if (!_appMods.TryGetValue(titleId, out ModCache mods) || mods.ExefsDirs.Count == 0)
            {
                return modLoadResult;
            }

            if (nsos.Length != ProcessConst.ExeFsPrefixes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(nsos), nsos.Length, "NSO Count is incorrect");
            }

            var exeMods = mods.ExefsDirs;

            foreach (var mod in exeMods)
            {
                for (int i = 0; i < ProcessConst.ExeFsPrefixes.Length; ++i)
                {
                    var nsoName = ProcessConst.ExeFsPrefixes[i];

                    FileInfo nsoFile = new(Path.Combine(mod.Path.FullName, nsoName));
                    if (nsoFile.Exists)
                    {
                        if (modLoadResult.Replaces[1 << i])
                        {
                            Logger.Warning?.Print(LogClass.ModLoader, $"Multiple replacements to '{nsoName}'");

                            continue;
                        }

                        modLoadResult.Replaces[1 << i] = true;

                        nsos[i] = new NsoExecutable(nsoFile.OpenRead().AsStorage(), nsoName);
                        Logger.Info?.Print(LogClass.ModLoader, $"NSO '{nsoName}' replaced");
                    }

                    modLoadResult.Stubs[1 << i] |= File.Exists(Path.Combine(mod.Path.FullName, nsoName + StubExtension));
                }

                FileInfo npdmFile = new(Path.Combine(mod.Path.FullName, "main.npdm"));
                if (npdmFile.Exists)
                {
                    if (modLoadResult.Npdm != null)
                    {
                        Logger.Warning?.Print(LogClass.ModLoader, "Multiple replacements to 'main.npdm'");

                        continue;
                    }

                    modLoadResult.Npdm = new MetaLoader();
                    modLoadResult.Npdm.Load(File.ReadAllBytes(npdmFile.FullName));

                    Logger.Info?.Print(LogClass.ModLoader, "main.npdm replaced");
                }
            }

            for (int i = ProcessConst.ExeFsPrefixes.Length - 1; i >= 0; --i)
            {
                if (modLoadResult.Stubs[1 << i] && !modLoadResult.Replaces[1 << i]) // Prioritizes replacements over stubs
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"    NSO '{nsos[i].Name}' stubbed");
                    nsos[i] = null;
                }
            }

            return modLoadResult;
        }

        internal void ApplyNroPatches(NroExecutable nro)
        {
            var nroPatches = _patches.NroPatches;

            if (nroPatches.Count == 0)
            {
                return;
            }

            // NRO patches aren't offset relative to header unlike NSO
            // according to Atmosphere's ro patcher module
            ApplyProgramPatches(nroPatches, 0, nro);
        }

        internal bool ApplyNsoPatches(ulong titleId, params IExecutable[] programs)
        {
            IEnumerable<Mod<DirectoryInfo>> nsoMods = _patches.NsoPatches;

            if (_appMods.TryGetValue(titleId, out ModCache mods))
            {
                nsoMods = nsoMods.Concat(mods.ExefsDirs);
            }

            // NSO patches are created with offset 0 according to Atmosphere's patcher module
            // But `Program` doesn't contain the header which is 0x100 bytes. So, we adjust for that here
            return ApplyProgramPatches(nsoMods, 0x100, programs);
        }

        internal void LoadCheats(ulong titleId, ProcessTamperInfo tamperInfo, TamperMachine tamperMachine)
        {
            if (tamperInfo?.BuildIds == null || tamperInfo.CodeAddresses == null)
            {
                Logger.Error?.Print(LogClass.ModLoader, "Unable to install cheat because the associated process is invalid");

                return;
            }

            Logger.Info?.Print(LogClass.ModLoader, $"Build ids found for title {titleId:X16}:\n    {String.Join("\n    ", tamperInfo.BuildIds)}");

            if (!_appMods.TryGetValue(titleId, out ModCache mods) || mods.Cheats.Count == 0)
            {
                return;
            }

            var cheats = mods.Cheats;
            var processExes = tamperInfo.BuildIds.Zip(tamperInfo.CodeAddresses, (k, v) => new { k, v })
                .ToDictionary(x => x.k[..Math.Min(Cheat.CheatIdSize, x.k.Length)], x => x.v);

            foreach (var cheat in cheats)
            {
                string cheatId = Path.GetFileNameWithoutExtension(cheat.Path.Name).ToUpper();

                if (!processExes.TryGetValue(cheatId, out ulong exeAddress))
                {
                    Logger.Warning?.Print(LogClass.ModLoader, $"Skipping cheat '{cheat.Name}' because no executable matches its BuildId {cheatId} (check if the game title and version are correct)");

                    continue;
                }

                Logger.Info?.Print(LogClass.ModLoader, $"Installing cheat '{cheat.Name}'");

                tamperMachine.InstallAtmosphereCheat(cheat.Name, cheatId, cheat.Instructions, tamperInfo, exeAddress);
            }

            EnableCheats(titleId, tamperMachine);
        }

        internal static void EnableCheats(ulong titleId, TamperMachine tamperMachine)
        {
            var contentDirectory = FindTitleDir(new DirectoryInfo(Path.Combine(GetModsBasePath(), AmsContentsDir)), $"{titleId:x16}");
            string enabledCheatsPath = Path.Combine(contentDirectory.FullName, CheatDir, "enabled.txt");

            if (File.Exists(enabledCheatsPath))
            {
                tamperMachine.EnableCheats(File.ReadAllLines(enabledCheatsPath));
            }
        }

        private static bool ApplyProgramPatches(IEnumerable<Mod<DirectoryInfo>> mods, int protectedOffset, params IExecutable[] programs)
        {
            int count = 0;

            MemPatch[] patches = new MemPatch[programs.Length];

            for (int i = 0; i < patches.Length; ++i)
            {
                patches[i] = new MemPatch();
            }

            var buildIds = programs.Select(p => p switch
            {
                NsoExecutable nso => Convert.ToHexString(nso.BuildId.ItemsRo.ToArray()).TrimEnd('0'),
                NroExecutable nro => Convert.ToHexString(nro.Header.BuildId).TrimEnd('0'),
                _ => string.Empty,
            }).ToList();

            int GetIndex(string buildId) => buildIds.FindIndex(id => id == buildId); // O(n) but list is small

            // Collect patches
            foreach (var mod in mods)
            {
                var patchDir = mod.Path;
                foreach (var patchFile in patchDir.EnumerateFiles())
                {
                    if (StrEquals(".ips", patchFile.Extension)) // IPS|IPS32
                    {
                        string filename = Path.GetFileNameWithoutExtension(patchFile.FullName).Split('.')[0];
                        string buildId = filename.TrimEnd('0');

                        int index = GetIndex(buildId);
                        if (index == -1)
                        {
                            continue;
                        }

                        Logger.Info?.Print(LogClass.ModLoader, $"Matching IPS patch '{patchFile.Name}' in '{mod.Name}' bid={buildId}");

                        using var fs = patchFile.OpenRead();
                        using var reader = new BinaryReader(fs);

                        var patcher = new IpsPatcher(reader);
                        patcher.AddPatches(patches[index]);
                    }
                    else if (StrEquals(".pchtxt", patchFile.Extension)) // IPSwitch
                    {
                        using var fs = patchFile.OpenRead();
                        using var reader = new StreamReader(fs);

                        var patcher = new IPSwitchPatcher(reader);

                        int index = GetIndex(patcher.BuildId);
                        if (index == -1)
                        {
                            continue;
                        }

                        Logger.Info?.Print(LogClass.ModLoader, $"Matching IPSwitch patch '{patchFile.Name}' in '{mod.Name}' bid={patcher.BuildId}");

                        patcher.AddPatches(patches[index]);
                    }
                }
            }

            // Apply patches
            for (int i = 0; i < programs.Length; ++i)
            {
                count += patches[i].Patch(programs[i].Program, protectedOffset);
            }

            return count > 0;
        }
    }
}
