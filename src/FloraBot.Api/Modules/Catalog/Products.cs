using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Catalog;

public sealed record ProductIdResponse(Guid Id);

public sealed record CreateProductRequest(
    Guid Id,
    string Name,
    string? Description,
    long Price,
    List<string> Tags,
    decimal LengthCm,
    decimal WidthCm,
    decimal HeightCm);

public sealed record UpdateProductRequest(
    string Name,
    string? Description,
    long Price,
    List<string> Tags,
    decimal LengthCm,
    decimal WidthCm,
    decimal HeightCm);

public static class Products
{
    public static void MapProducts(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/products", CreateAsync)
            .RequireAuthorization("Seller", "SameSeller")
            .WithTags("Catalog")
            .Produces<ProductIdResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPut("/api/sellers/{sellerId:guid}/products/{productId:guid}", UpdateAsync)
            .RequireAuthorization("Seller", "SameSeller")
            .WithTags("Catalog")
            .Produces<ProductIdResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> CreateAsync(Guid sellerId, CreateProductRequest request, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (request.Id == Guid.Empty) return Results.BadRequest();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) return Results.BadRequest();
        if (request.Description is not null && request.Description.Length > 2000) return Results.BadRequest();
        if (request.Price <= 0) return Results.BadRequest();
        if (!ValidDimensions(request.LengthCm, request.WidthCm, request.HeightCm)) return Results.BadRequest();
        if (request.Tags is null || request.Tags.Count > 20 || request.Tags.Any(t => t is null)) return Results.BadRequest();
        var trimmedTags = request.Tags.Select(t => t.Trim()).ToList();
        if (trimmedTags.Any(t => t.Length is < 1 or > 64) || trimmedTags.Distinct().Count() != trimmedTags.Count)
            return Results.BadRequest();

        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId)) return Results.Unauthorized();

        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT flow.create_artificial_product(@id, @seller, @actor, @name, @desc, @price, @tags, @length, @width, @height)", connection, transaction);
            command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = request.Id;
            command.Parameters.Add("seller", NpgsqlDbType.Uuid).Value = sellerId;
            command.Parameters.Add("actor", NpgsqlDbType.Uuid).Value = actorId;
            command.Parameters.Add("name", NpgsqlDbType.Text).Value = request.Name.Trim();
            command.Parameters.Add("desc", NpgsqlDbType.Text).Value = (object?)request.Description ?? DBNull.Value;
            command.Parameters.Add("price", NpgsqlDbType.Bigint).Value = request.Price;
            command.Parameters.Add("tags", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = trimmedTags.ToArray();
            command.Parameters.Add("length", NpgsqlDbType.Numeric).Value = request.LengthCm;
            command.Parameters.Add("width", NpgsqlDbType.Numeric).Value = request.WidthCm;
            command.Parameters.Add("height", NpgsqlDbType.Numeric).Value = request.HeightCm;

            var resultId = (Guid)(await command.ExecuteScalarAsync(ct))!;
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/sellers/{sellerId}/products/{resultId}", new ProductIdResponse(resultId));
        }
        catch (PostgresException ex) when (Handled(ex))
        {
            return HandleProblem(ex);
        }
    }

    private static async Task<IResult> UpdateAsync(Guid sellerId, Guid productId, UpdateProductRequest request, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (productId == Guid.Empty) return Results.BadRequest();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) return Results.BadRequest();
        if (request.Description is not null && request.Description.Length > 2000) return Results.BadRequest();
        if (request.Price <= 0) return Results.BadRequest();
        if (!ValidDimensions(request.LengthCm, request.WidthCm, request.HeightCm)) return Results.BadRequest();
        if (request.Tags is null || request.Tags.Count > 20 || request.Tags.Any(t => t is null)) return Results.BadRequest();
        var trimmedTags = request.Tags.Select(t => t.Trim()).ToList();
        if (trimmedTags.Any(t => t.Length is < 1 or > 64) || trimmedTags.Distinct().Count() != trimmedTags.Count)
            return Results.BadRequest();

        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId)) return Results.Unauthorized();

        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT flow.update_artificial_product(@product, @seller, @actor, @name, @desc, @price, @tags, @length, @width, @height)", connection, transaction);
            command.Parameters.Add("product", NpgsqlDbType.Uuid).Value = productId;
            command.Parameters.Add("seller", NpgsqlDbType.Uuid).Value = sellerId;
            command.Parameters.Add("actor", NpgsqlDbType.Uuid).Value = actorId;
            command.Parameters.Add("name", NpgsqlDbType.Text).Value = request.Name.Trim();
            command.Parameters.Add("desc", NpgsqlDbType.Text).Value = (object?)request.Description ?? DBNull.Value;
            command.Parameters.Add("price", NpgsqlDbType.Bigint).Value = request.Price;
            command.Parameters.Add("tags", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = trimmedTags.ToArray();
            command.Parameters.Add("length", NpgsqlDbType.Numeric).Value = request.LengthCm;
            command.Parameters.Add("width", NpgsqlDbType.Numeric).Value = request.WidthCm;
            command.Parameters.Add("height", NpgsqlDbType.Numeric).Value = request.HeightCm;

            var resultId = (Guid)(await command.ExecuteScalarAsync(ct))!;
            await transaction.CommitAsync(ct);
            return Results.Ok(new ProductIdResponse(resultId));
        }
        catch (PostgresException ex) when (Handled(ex))
        {
            return HandleProblem(ex);
        }
    }

    private static bool ValidDimensions(decimal length, decimal width, decimal height) =>
        length is >= 0.01m and <= 1000m && decimal.Round(length, 2) == length &&
        width is >= 0.01m and <= 1000m && decimal.Round(width, 2) == width &&
        height is >= 0.01m and <= 1000m && decimal.Round(height, 2) == height;

    private static bool Handled(PostgresException ex) =>
        ex.SqlState is "42501" or "P0002" or "40001" or "23505" or "22023" or "23514" or "23503" or "P0001";

    private static IResult HandleProblem(PostgresException ex) => ex.SqlState switch
    {
        "42501" => Results.Forbid(),
        "P0002" => Results.NotFound(),
        "40001" or "23505" or "P0001" => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.MessageText),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.MessageText)
    };
}
