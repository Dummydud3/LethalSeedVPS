public enum PostgresSeedSyncConflictMode
{
    Upsert,
    Skip
}

public sealed class VpsSeedItemCount
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int ItemCount { get; init; }
}

public sealed class VpsSeedRow
{
    public int Seed { get; init; }
    public int RunSeed { get; init; }
    public int WeatherSeed { get; init; }
    public string Weather { get; init; } = string.Empty;
    public int ScrapCount { get; init; }
    public int TotalScrapValue { get; init; }
    public bool GoldbarOnly { get; init; }
    public int InsideEnemyRolls { get; init; }
    public int OutsideEnemyRolls { get; init; }
    public int DaytimeEnemyRolls { get; init; }
    public string FirstInsideSpawnTime { get; init; } = string.Empty;
    public string FirstOutsideSpawnTime { get; init; } = string.Empty;
    public string FirstDaytimeSpawnTime { get; init; } = string.Empty;
    public int EstimatedOutsideHazards { get; init; }
    public bool PowerOffAtStart { get; init; }
    public int KeyCount { get; init; }
    public int DungeonSeed { get; init; }
    public int DungeonFlowId { get; init; }
    public string DungeonFlowName { get; init; } = string.Empty;
    public string DungeonFlowTheme { get; init; } = string.Empty;
    public bool ApparatusSpawned { get; init; }
    public int ApparatusValue { get; init; }
    public string? RollsJson { get; init; }
    public List<VpsSeedItemCount> ItemCounts { get; init; } = [];
}

public sealed class VpsSeedBatchUpsertRequest
{
    public string RulepackVersion { get; init; } = string.Empty;
    public string MoonId { get; init; } = string.Empty;
    public PostgresSeedSyncConflictMode ConflictMode { get; init; } = PostgresSeedSyncConflictMode.Upsert;
    public List<VpsSeedRow> Rows { get; init; } = [];
}

public sealed class VpsSeedBatchUpsertResponse
{
    public int ReceivedRows { get; init; }
    public int UpsertedRows { get; init; }
}

public sealed class GuiSeedRowDto
{
    public int Seed { get; init; }
    public int TotalScrapValue { get; init; }
    public int ScrapCount { get; init; }
    public string Weather { get; init; } = string.Empty;
    public int KeyCount { get; init; }
    public string DungeonFlowTheme { get; init; } = string.Empty;
    public int ApparatusValue { get; init; }
}

public sealed class GuiSeedPageDto
{
    public List<GuiSeedRowDto> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class VpsMoonProgressRow
{
    public string MoonId { get; init; } = string.Empty;
    public long SimulatedCount { get; init; }
}

public sealed class VpsMoonProgressResponse
{
    public string RulepackVersion { get; init; } = string.Empty;
    public List<VpsMoonProgressRow> Rows { get; init; } = [];
}

public static class SeedExportSchema
{
    public const string PostgresCreateTablesDdl = """
        CREATE TABLE IF NOT EXISTS seeds (
            rulepack_version TEXT NOT NULL,
            moon_id TEXT NOT NULL,
            seed INTEGER NOT NULL,
            run_seed INTEGER NOT NULL,
            weather_seed INTEGER NOT NULL,
            weather TEXT NOT NULL,
            scrap_count INTEGER NOT NULL,
            total_scrap_value INTEGER NOT NULL,
            goldbar_only INTEGER NOT NULL,
            inside_enemy_rolls INTEGER NOT NULL,
            outside_enemy_rolls INTEGER NOT NULL,
            daytime_enemy_rolls INTEGER NOT NULL,
            first_inside_spawn_time TEXT,
            first_outside_spawn_time TEXT,
            first_daytime_spawn_time TEXT,
            estimated_outside_hazards INTEGER NOT NULL,
            power_off_at_start INTEGER NOT NULL,
            key_count INTEGER NOT NULL,
            dungeon_seed INTEGER NOT NULL,
            dungeon_flow_id INTEGER NOT NULL,
            dungeon_flow_name TEXT NOT NULL,
            dungeon_flow_theme TEXT NOT NULL,
            apparatus_spawned INTEGER NOT NULL,
            apparatus_value INTEGER NOT NULL,
            rolls_json TEXT,
            PRIMARY KEY (rulepack_version, moon_id, seed)
        );
        CREATE TABLE IF NOT EXISTS seed_item_counts (
            rulepack_version TEXT NOT NULL,
            moon_id TEXT NOT NULL,
            seed INTEGER NOT NULL,
            item_id INTEGER NOT NULL,
            item_name TEXT NOT NULL,
            item_count INTEGER NOT NULL,
            PRIMARY KEY (rulepack_version, moon_id, seed, item_id)
        );
        """;

    public const string PostgresCreateIndexesDdl = """
        CREATE INDEX IF NOT EXISTS idx_pg_seeds_lookup ON seeds(rulepack_version, moon_id, seed);
        CREATE INDEX IF NOT EXISTS idx_pg_seeds_total_scrap ON seeds(rulepack_version, moon_id, total_scrap_value DESC);
        CREATE INDEX IF NOT EXISTS idx_pg_seeds_scrap_count ON seeds(rulepack_version, moon_id, scrap_count DESC);
        CREATE INDEX IF NOT EXISTS idx_pg_seeds_weather ON seeds(rulepack_version, moon_id, weather);
        CREATE INDEX IF NOT EXISTS idx_pg_seeds_key_count ON seeds(rulepack_version, moon_id, key_count DESC);
        CREATE INDEX IF NOT EXISTS idx_pg_seeds_dungeon_flow ON seeds(rulepack_version, moon_id, dungeon_flow_theme, dungeon_flow_id);
        CREATE INDEX IF NOT EXISTS idx_pg_seed_item_counts_item ON seed_item_counts(rulepack_version, moon_id, item_id, item_count DESC);
        """;
}
