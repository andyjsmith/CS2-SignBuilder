using System;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.PSI.Common;
using UnityEngine;

namespace HighwaySigns.Spike
{
    /// <summary>
    /// A mod-owned asset database rooted at ModsData/HighwaySigns/Cache, so generated meshes and
    /// materials never land in the player's ImportedData folder. Mirrors how the game defines its
    /// own "User" database (a FileSystemDataSource behind an IAssetDatabaseDescriptor).
    /// </summary>
    public readonly struct HighwaySignsCache : IAssetDatabaseDescriptor<HighwaySignsCache>, IEquatable<HighwaySignsCache>
    {
        public static string RootPath => Path.Combine(Application.persistentDataPath, "ModsData", "HighwaySigns", "Cache");

        public DlcId dlcId => DlcId.Invalid;
        public bool canWriteSettings => false;
        public string name => "HighwaySignsCache";
        public IAssetFactory assetFactory => DefaultAssetFactory.instance;

        public IDataSourceProvider dataSourceProvider
        {
            get
            {
                Directory.CreateDirectory(RootPath);
                return new FileSystemDataSource(name, RootPath, assetFactory, 0L);
            }
        }

        public bool Equals(HighwaySignsCache other) => true;
        public override bool Equals(object obj) => obj is HighwaySignsCache;
        public override int GetHashCode() => GetType().GetHashCode();
    }
}
