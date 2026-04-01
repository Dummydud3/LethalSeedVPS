using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
var app = builder.Build();

var pgConnectionString = Environment.GetEnvironmentVariable("LETHAL_SIM_PG_CONNECTION");
if (string.IsNullOrWhiteSpace(pgConnectionString))
{
    throw new InvalidOperationException("Set LETHAL_SIM_PG_CONNECTION for PostgreSQL access.");
}

var apiKey = Environment.GetEnvironmentVariable("LETHAL_SIM_API_KEY");
var adminUser = Environment.GetEnvironmentVariable("LETHAL_SIM_ADMIN_USERNAME");
var adminPass = Environment.GetEnvironmentVariable("LETHAL_SIM_ADMIN_PASSWORD");
app.Use(async (context, next) =>
{
    if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(adminUser))
    {
        await next();
        return;
    }

    var authorized = false;
    if (!string.IsNullOrWhiteSpace(apiKey) &&
        context.Request.Headers.TryGetValue("X-Api-Key", out var incoming) &&
        string.Equals(incoming.ToString(), apiKey, StringComparison.Ordinal))
    {
        authorized = true;
    }

    if (!authorized &&
        !string.IsNullOrWhiteSpace(adminUser) &&
        !string.IsNullOrWhiteSpace(adminPass) &&
        context.Request.Headers.TryGetValue("Authorization", out var authHeader))
    {
        var value = authHeader.ToString();
        if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = value["Basic ".Length..].Trim();
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var idx = decoded.IndexOf(':');
                if (idx > 0)
                {
                    var user = decoded[..idx];
                    var pass = decoded[(idx + 1)..];
                    authorized = string.Equals(user, adminUser, StringComparison.Ordinal) &&
                                 string.Equals(pass, adminPass, StringComparison.Ordinal);
                }
            }
            catch
            {
            }
        }
    }

    if (!authorized)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unauthorized.");
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
          @p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23,@p24,@p25
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
        ) VALUES (@p1,@p2,@p3,@p4,@p5,@p6) {itemConflict};
        """;
    await using var itemCmd = new NpgsqlCommand(itemSql, pg, tx);
    for (var i = 1; i <= 6; i++)
    {
        itemCmd.Parameters.Add(new NpgsqlParameter($"${i}", DBNull.Value));
    }

    await using var deleteItems = new NpgsqlCommand(
        "DELETE FROM seed_item_counts WHERE rulepack_version=@p1 AND moon_id=@p2 AND seed=@p3;",
        pg,
        tx);
    deleteItems.Parameters.Add(new NpgsqlParameter("@p1", DBNull.Value));
    deleteItems.Parameters.Add(new NpgsqlParameter("@p2", DBNull.Value));
    deleteItems.Parameters.Add(new NpgsqlParameter("@p3", DBNull.Value));

    foreach (var row in request.Rows)
    {
        seedCmd.Parameters["@p1"].Value = request.RulepackVersion;
        seedCmd.Parameters["@p2"].Value = request.MoonId;
        seedCmd.Parameters["@p3"].Value = row.Seed;
        seedCmd.Parameters["@p4"].Value = row.RunSeed;
        seedCmd.Parameters["@p5"].Value = row.WeatherSeed;
        seedCmd.Parameters["@p6"].Value = row.Weather;
        seedCmd.Parameters["@p7"].Value = row.ScrapCount;
        seedCmd.Parameters["@p8"].Value = row.TotalScrapValue;
        seedCmd.Parameters["@p9"].Value = row.GoldbarOnly ? 1 : 0;
        seedCmd.Parameters["@p10"].Value = row.InsideEnemyRolls;
        seedCmd.Parameters["@p11"].Value = row.OutsideEnemyRolls;
        seedCmd.Parameters["@p12"].Value = row.DaytimeEnemyRolls;
        seedCmd.Parameters["@p13"].Value = row.FirstInsideSpawnTime;
        seedCmd.Parameters["@p14"].Value = row.FirstOutsideSpawnTime;
        seedCmd.Parameters["@p15"].Value = row.FirstDaytimeSpawnTime;
        seedCmd.Parameters["@p16"].Value = row.EstimatedOutsideHazards;
        seedCmd.Parameters["@p17"].Value = row.PowerOffAtStart ? 1 : 0;
        seedCmd.Parameters["@p18"].Value = row.KeyCount;
        seedCmd.Parameters["@p19"].Value = row.DungeonSeed;
        seedCmd.Parameters["@p20"].Value = row.DungeonFlowId;
        seedCmd.Parameters["@p21"].Value = row.DungeonFlowName;
        seedCmd.Parameters["@p22"].Value = row.DungeonFlowTheme;
        seedCmd.Parameters["@p23"].Value = row.ApparatusSpawned ? 1 : 0;
        seedCmd.Parameters["@p24"].Value = row.ApparatusValue;
        seedCmd.Parameters["@p25"].Value = string.IsNullOrWhiteSpace(row.RollsJson) ? DBNull.Value : row.RollsJson;
        await seedCmd.ExecuteNonQueryAsync(ct);

        if (request.ConflictMode == PostgresSeedSyncConflictMode.Upsert)
        {
            deleteItems.Parameters["@p1"].Value = request.RulepackVersion;
            deleteItems.Parameters["@p2"].Value = request.MoonId;
            deleteItems.Parameters["@p3"].Value = row.Seed;
            await deleteItems.ExecuteNonQueryAsync(ct);
        }

        foreach (var item in row.ItemCounts)
        {
            itemCmd.Parameters["@p1"].Value = request.RulepackVersion;
            itemCmd.Parameters["@p2"].Value = request.MoonId;
            itemCmd.Parameters["@p3"].Value = row.Seed;
            itemCmd.Parameters["@p4"].Value = item.ItemId;
            itemCmd.Parameters["@p5"].Value = item.ItemName;
            itemCmd.Parameters["@p6"].Value = item.ItemCount;
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
            WHERE rulepack_version = @p1
              AND moon_id = @p2
              AND (@p3::int IS NULL OR total_scrap_value >= @p3)
              AND (@p4::int IS NULL OR total_scrap_value <= @p4);
            """,
            pg);
        count.Parameters.AddWithValue("@p1", rulepackVersion);
        count.Parameters.AddWithValue("@p2", moonId);
        count.Parameters.AddWithValue("@p3", (object?)minTotalScrapValue ?? DBNull.Value);
        count.Parameters.AddWithValue("@p4", (object?)maxTotalScrapValue ?? DBNull.Value);
        var totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(ct) ?? 0);

        var offset = (page - 1) * pageSize;
        var sql = $"""
            SELECT seed, total_scrap_value, scrap_count, weather, key_count, dungeon_flow_theme, apparatus_value
            FROM seeds
            WHERE rulepack_version = @p1
              AND moon_id = @p2
              AND (@p3::int IS NULL OR total_scrap_value >= @p3)
              AND (@p4::int IS NULL OR total_scrap_value <= @p4)
            ORDER BY {orderBy} {direction}, seed ASC
            LIMIT @p5 OFFSET @p6;
            """;
        await using var query = new NpgsqlCommand(sql, pg);
        query.Parameters.AddWithValue("@p1", rulepackVersion);
        query.Parameters.AddWithValue("@p2", moonId);
        query.Parameters.AddWithValue("@p3", (object?)minTotalScrapValue ?? DBNull.Value);
        query.Parameters.AddWithValue("@p4", (object?)maxTotalScrapValue ?? DBNull.Value);
        query.Parameters.AddWithValue("@p5", pageSize);
        query.Parameters.AddWithValue("@p6", offset);

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
        WHERE rulepack_version = @p1
        GROUP BY moon_id
        ORDER BY moon_id;
        """,
        pg);
    cmd.Parameters.AddWithValue("@p1", rulepackVersion);
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
