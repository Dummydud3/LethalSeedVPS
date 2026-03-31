using LethalSeedSimulator.Core;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
var app = builder.Build();

var pgConnectionString = Environment.GetEnvironmentVariable("LETHAL_SIM_PG_CONNECTION");
if (string.IsNullOrWhiteSpace(pgConnectionString))
{
    throw new InvalidOperationException("Set LETHAL_SIM_PG_CONNECTION for PostgreSQL access.");
}

var apiKey = Environment.GetEnvironmentVariable("LETHAL_SIM_API_KEY");
app.Use(async (context, next) =>
{
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-Api-Key", out var incoming) ||
        !string.Equals(incoming.ToString(), apiKey, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Missing or invalid API key.");
        return;
    }

    await next();
});

await EnsureSchemaAsync(pgConnectionString);
app.MapGet("/health", () => Results.Ok(new { ok = true, utc = DateTime.UtcNow }));

app.MapPost("/api/seeds/batch", async (VpsSeedBatchUpsertRequest request, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RulepackVersion) || string.IsNullOrWhiteSpace(request.MoonId))
    {
        return Results.BadRequest("rulepackVersion and moonId are required.");
    }

    if (request.Rows.Count == 0)
    {
        return Results.Ok(new VpsSeedBatchUpsertResponse { ReceivedRows = 0, UpsertedRows = 0 });
    }

    var seedConflict = request.ConflictMode == PostgresSeedSyncConflictMode.Upsert
        ? """
          ON CONFLICT (rulepack_version, moon_id, seed) DO UPDATE SET
            run_seed = EXCLUDED.run_seed,
            weather_seed = EXCLUDED.weather_seed,
            weather = EXCLUDED.weather,
            scrap_count = EXCLUDED.scrap_count,
            total_scrap_value = EXCLUDED.total_scrap_value,
            goldbar_only = EXCLUDED.goldbar_only,
            inside_enemy_rolls = EXCLUDED.inside_enemy_rolls,
            outside_enemy_rolls = EXCLUDED.outside_enemy_rolls,
            daytime_enemy_rolls = EXCLUDED.daytime_enemy_rolls,
            first_inside_spawn_time = EXCLUDED.first_inside_spawn_time,
            first_outside_spawn_time = EXCLUDED.first_outside_spawn_time,
            first_daytime_spawn_time = EXCLUDED.first_daytime_spawn_time,
            estimated_outside_hazards = EXCLUDED.estimated_outside_hazards,
            power_off_at_start = EXCLUDED.power_off_at_start,
            key_count = EXCLUDED.key_count,
            dungeon_seed = EXCLUDED.dungeon_seed,
            dungeon_flow_id = EXCLUDED.dungeon_flow_id,
            dungeon_flow_name = EXCLUDED.dungeon_flow_name,
            dungeon_flow_theme = EXCLUDED.dungeon_flow_theme,
            apparatus_spawned = EXCLUDED.apparatus_spawned,
            apparatus_value = EXCLUDED.apparatus_value,
            rolls_json = EXCLUDED.rolls_json
          """
        : "ON CONFLICT (rulepack_version, moon_id, seed) DO NOTHING";

    var itemConflict = request.ConflictMode == PostgresSeedSyncConflictMode.Upsert
        ? """
          ON CONFLICT (rulepack_version, moon_id, seed, item_id) DO UPDATE SET
            item_name = EXCLUDED.item_name,
            item_count = EXCLUDED.item_count
          """
        : "ON CONFLICT (rulepack_version, moon_id, seed, item_id) DO NOTHING";

    await using var pg = new NpgsqlConnection(pgConnectionString);
    await pg.OpenAsync(ct);
    await using var tx = await pg.BeginTransactionAsync(ct);

    var seedSql = $"""
        INSERT INTO seeds (
          rulepack_version, moon_id, seed, run_seed, weather_seed, weather, scrap_count, total_scrap_value, goldbar_only,
          inside_enemy_rolls, outside_enemy_rolls, daytime_enemy_rolls,
          first_inside_spawn_time, first_outside_spawn_time, first_daytime_spawn_time,
          estimated_outside_hazards, power_off_at_start, key_count, dungeon_seed, dungeon_flow_id,
          dungeon_flow_name, dungeon_flow_theme, apparatus_spawned, apparatus_value, rolls_json
        ) VALUES (
          $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25
        ) {seedConflict};
        """;
    await using var seedCmd = new NpgsqlCommand(seedSql, pg, tx);
    for (var i = 1; i <= 25; i++)
    {
        seedCmd.Parameters.Add(new NpgsqlParameter($"${i}", DBNull.Value));
    }

    var itemSql = $"""
        INSERT INTO seed_item_counts (
            rulepack_version, moon_id, seed, item_id, item_name, item_count
        ) VALUES ($1,$2,$3,$4,$5,$6) {itemConflict};
        """;
    await using var itemCmd = new NpgsqlCommand(itemSql, pg, tx);
    for (var i = 1; i <= 6; i++)
    {
        itemCmd.Parameters.Add(new NpgsqlParameter($"${i}", DBNull.Value));
    }

    await using var deleteItems = new NpgsqlCommand(
        "DELETE FROM seed_item_counts WHERE rulepack_version=$1 AND moon_id=$2 AND seed=$3;",
        pg,
        tx);
    deleteItems.Parameters.Add(new NpgsqlParameter("$1", DBNull.Value));
    deleteItems.Parameters.Add(new NpgsqlParameter("$2", DBNull.Value));
    deleteItems.Parameters.Add(new NpgsqlParameter("$3", DBNull.Value));

    foreach (var row in request.Rows)
    {
        seedCmd.Parameters["$1"].Value = request.RulepackVersion;
        seedCmd.Parameters["$2"].Value = request.MoonId;
        seedCmd.Parameters["$3"].Value = row.Seed;
        seedCmd.Parameters["$4"].Value = row.RunSeed;
        seedCmd.Parameters["$5"].Value = row.WeatherSeed;
        seedCmd.Parameters["$6"].Value = row.Weather;
        seedCmd.Parameters["$7"].Value = row.ScrapCount;
        seedCmd.Parameters["$8"].Value = row.TotalScrapValue;
        seedCmd.Parameters["$9"].Value = row.GoldbarOnly ? 1 : 0;
        seedCmd.Parameters["$10"].Value = row.InsideEnemyRolls;
        seedCmd.Parameters["$11"].Value = row.OutsideEnemyRolls;
        seedCmd.Parameters["$12"].Value = row.DaytimeEnemyRolls;
        seedCmd.Parameters["$13"].Value = row.FirstInsideSpawnTime;
        seedCmd.Parameters["$14"].Value = row.FirstOutsideSpawnTime;
        seedCmd.Parameters["$15"].Value = row.FirstDaytimeSpawnTime;
        seedCmd.Parameters["$16"].Value = row.EstimatedOutsideHazards;
        seedCmd.Parameters["$17"].Value = row.PowerOffAtStart ? 1 : 0;
        seedCmd.Parameters["$18"].Value = row.KeyCount;
        seedCmd.Parameters["$19"].Value = row.DungeonSeed;
        seedCmd.Parameters["$20"].Value = row.DungeonFlowId;
        seedCmd.Parameters["$21"].Value = row.DungeonFlowName;
        seedCmd.Parameters["$22"].Value = row.DungeonFlowTheme;
        seedCmd.Parameters["$23"].Value = row.ApparatusSpawned ? 1 : 0;
        seedCmd.Parameters["$24"].Value = row.ApparatusValue;
        seedCmd.Parameters["$25"].Value = string.IsNullOrWhiteSpace(row.RollsJson) ? DBNull.Value : row.RollsJson;
        await seedCmd.ExecuteNonQueryAsync(ct);

        if (request.ConflictMode == PostgresSeedSyncConflictMode.Upsert)
        {
            deleteItems.Parameters["$1"].Value = request.RulepackVersion;
            deleteItems.Parameters["$2"].Value = request.MoonId;
            deleteItems.Parameters["$3"].Value = row.Seed;
            await deleteItems.ExecuteNonQueryAsync(ct);
        }

        foreach (var item in row.ItemCounts)
        {
            itemCmd.Parameters["$1"].Value = request.RulepackVersion;
            itemCmd.Parameters["$2"].Value = request.MoonId;
            itemCmd.Parameters["$3"].Value = row.Seed;
            itemCmd.Parameters["$4"].Value = item.ItemId;
            itemCmd.Parameters["$5"].Value = item.ItemName;
            itemCmd.Parameters["$6"].Value = item.ItemCount;
            await itemCmd.ExecuteNonQueryAsync(ct);
        }
    }

    await tx.CommitAsync(ct);
    return Results.Ok(new VpsSeedBatchUpsertResponse
    {
        ReceivedRows = request.Rows.Count,
        UpsertedRows = request.Rows.Count
    });
});

app.MapGet(
    "/api/seeds/query",
    async (
        string rulepackVersion,
        string moonId,
        string? sortColumn,
        bool sortDescending,
        int? minTotalScrapValue,
        int? maxTotalScrapValue,
        int page,
        int pageSize,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(rulepackVersion) || string.IsNullOrWhiteSpace(moonId))
        {
            return Results.BadRequest("rulepackVersion and moonId are required.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 5000);

        var allowedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["seed"] = "seed",
            ["total_scrap_value"] = "total_scrap_value",
            ["scrap_count"] = "scrap_count",
            ["key_count"] = "key_count",
            ["apparatus_value"] = "apparatus_value",
            ["weather"] = "weather",
            ["dungeon_flow_theme"] = "dungeon_flow_theme"
        };
        var orderBy = allowedColumns.TryGetValue(sortColumn ?? "seed", out var mapped) ? mapped : "seed";
        var direction = sortDescending ? "DESC" : "ASC";

        await using var pg = new NpgsqlConnection(pgConnectionString);
        await pg.OpenAsync(ct);

        await using var count = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM seeds
            WHERE rulepack_version = $1
              AND moon_id = $2
              AND ($3::int IS NULL OR total_scrap_value >= $3)
              AND ($4::int IS NULL OR total_scrap_value <= $4);
            """,
            pg);
        count.Parameters.AddWithValue("$1", rulepackVersion);
        count.Parameters.AddWithValue("$2", moonId);
        count.Parameters.AddWithValue("$3", (object?)minTotalScrapValue ?? DBNull.Value);
        count.Parameters.AddWithValue("$4", (object?)maxTotalScrapValue ?? DBNull.Value);
        var totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(ct) ?? 0);

        var offset = (page - 1) * pageSize;
        var sql = $"""
            SELECT seed, total_scrap_value, scrap_count, weather, key_count, dungeon_flow_theme, apparatus_value
            FROM seeds
            WHERE rulepack_version = $1
              AND moon_id = $2
              AND ($3::int IS NULL OR total_scrap_value >= $3)
              AND ($4::int IS NULL OR total_scrap_value <= $4)
            ORDER BY {orderBy} {direction}, seed ASC
            LIMIT $5 OFFSET $6;
            """;
        await using var query = new NpgsqlCommand(sql, pg);
        query.Parameters.AddWithValue("$1", rulepackVersion);
        query.Parameters.AddWithValue("$2", moonId);
        query.Parameters.AddWithValue("$3", (object?)minTotalScrapValue ?? DBNull.Value);
        query.Parameters.AddWithValue("$4", (object?)maxTotalScrapValue ?? DBNull.Value);
        query.Parameters.AddWithValue("$5", pageSize);
        query.Parameters.AddWithValue("$6", offset);

        var rows = new List<GuiSeedRowDto>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new GuiSeedRowDto
            {
                Seed = reader.GetInt32(0),
                TotalScrapValue = reader.GetInt32(1),
                ScrapCount = reader.GetInt32(2),
                Weather = reader.GetString(3),
                KeyCount = reader.GetInt32(4),
                DungeonFlowTheme = reader.GetString(5),
                ApparatusValue = reader.GetInt32(6)
            });
        }

        return Results.Ok(new GuiSeedPageDto { Rows = rows, TotalCount = totalCount });
    });

app.MapGet("/api/moons/progress", async (string rulepackVersion, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(rulepackVersion))
    {
        return Results.BadRequest("rulepackVersion is required.");
    }

    await using var pg = new NpgsqlConnection(pgConnectionString);
    await pg.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
        """
        SELECT moon_id, COUNT(*) AS c
        FROM seeds
        WHERE rulepack_version = $1
        GROUP BY moon_id
        ORDER BY moon_id;
        """,
        pg);
    cmd.Parameters.AddWithValue("$1", rulepackVersion);
    var rows = new List<VpsMoonProgressRow>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        rows.Add(new VpsMoonProgressRow
        {
            MoonId = reader.GetString(0),
            SimulatedCount = reader.GetInt64(1)
        });
    }

    return Results.Ok(new VpsMoonProgressResponse
    {
        RulepackVersion = rulepackVersion,
        Rows = rows
    });
});

app.Run();

static async Task EnsureSchemaAsync(string connectionString)
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();
    await using var cmd = pg.CreateCommand();
    cmd.CommandText = SeedExportSchema.PostgresCreateTablesDdl + SeedExportSchema.PostgresCreateIndexesDdl;
    await cmd.ExecuteNonQueryAsync();
}
