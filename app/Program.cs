using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProductCatalogApi.Data;
using ProductCatalogApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Database=productsdb;Username=postgres;Password=postgrespassword";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure OpenTelemetry
var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "ProductCatalogApi";

builder.Services.Configure<OpenTelemetryLoggerOptions>(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otelServiceName))
    .WithLogging(logging => logging.AddOtlpExporter())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
var app = builder.Build();

// Retry database connection and run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    int retries = 10;
    while (retries > 0)
    {
        try
        {
            logger.LogInformation("Attempting to run database migrations...");
            db.Database.Migrate();
            logger.LogInformation("Database migrations applied successfully.");
            break;
        }
        catch (Exception ex)
        {
            retries--;
            logger.LogWarning(ex, "Failed to apply migrations. Retrying in 3 seconds... ({Retries} left)", retries);
            if (retries == 0)
            {
                logger.LogCritical(ex, "Could not apply database migrations after multiple retries. Exiting.");
                throw;
            }
            Thread.Sleep(3000);
        }
    }
}

app.MapGet("/", () => "Product Catalog API is running.");

// Group products endpoints
var productsGroup = app.MapGroup("/api/products");

// GET /api/products - Get all products
productsGroup.MapGet("/", async (AppDbContext db, ILogger<Program> logger) =>
{
    logger.LogInformation("START: Request to retrieve all products.");
    var products = await db.Products.ToListAsync();
    logger.LogInformation("END: Successfully retrieved {Count} products.", products.Count);
    return Results.Ok(products);
});

// GET /api/products/{id} - Get product by ID
productsGroup.MapGet("/{id:guid}", async (Guid id, AppDbContext db, ILogger<Program> logger) =>
{
    logger.LogInformation("START: Request to retrieve product with ID: {ProductId}", id);
    var product = await db.Products.FindAsync(id);
    if (product is null)
    {
        logger.LogWarning("END: Product with ID: {ProductId} not found.", id);
        return Results.NotFound();
    }
    logger.LogInformation("END: Successfully retrieved product with ID: {ProductId}", id);
    return Results.Ok(product);
});

// POST /api/products - Create a product
productsGroup.MapPost("/", async (CreateProductRequest request, AppDbContext db, ILogger<Program> logger) =>
{
    logger.LogInformation("START: Request to create a new product. Name: {ProductName}, Price: {ProductPrice}", request.Name, request.Price);
    
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        logger.LogWarning("END: Create product failed due to validation: Name is empty.");
        return Results.BadRequest("Product Name is required.");
    }
    if (request.Price < 0)
    {
        logger.LogWarning("END: Create product failed due to validation: Price is negative.");
        return Results.BadRequest("Product Price cannot be negative.");
    }

    var product = new Product
    {
        Id = Guid.NewGuid(),
        Name = request.Name,
        Description = request.Description,
        Price = request.Price
    };

    await db.Products.AddAsync(product);
    await db.SaveChangesAsync();

    logger.LogInformation("END: Successfully created product with ID: {ProductId}", product.Id);
    return Results.Created($"/api/products/{product.Id}", product);
});

// PUT /api/products/{id} - Update a product
productsGroup.MapPut("/{id:guid}", async (Guid id, UpdateProductRequest request, AppDbContext db, ILogger<Program> logger) =>
{
    logger.LogInformation("START: Request to update product with ID: {ProductId}", id);

    if (string.IsNullOrWhiteSpace(request.Name))
    {
        logger.LogWarning("END: Update product failed due to validation: Name is empty.");
        return Results.BadRequest("Product Name is required.");
    }
    if (request.Price < 0)
    {
        logger.LogWarning("END: Update product failed due to validation: Price is negative.");
        return Results.BadRequest("Product Price cannot be negative.");
    }

    var existingProduct = await db.Products.FindAsync(id);
    if (existingProduct is null)
    {
        logger.LogWarning("END: Update failed. Product with ID: {ProductId} not found.", id);
        return Results.NotFound();
    }

    existingProduct.Name = request.Name;
    existingProduct.Description = request.Description;
    existingProduct.Price = request.Price;

    await db.SaveChangesAsync();

    logger.LogInformation("END: Successfully updated product with ID: {ProductId}", id);
    return Results.NoContent();
});

// DELETE /api/products/{id} - Delete a product
productsGroup.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, ILogger<Program> logger) =>
{
    logger.LogInformation("START: Request to delete product with ID: {ProductId}", id);

    var product = await db.Products.FindAsync(id);
    if (product is null)
    {
        logger.LogWarning("END: Delete failed. Product with ID: {ProductId} not found.", id);
        return Results.NotFound();
    }

    db.Products.Remove(product);
    await db.SaveChangesAsync();

    logger.LogInformation("END: Successfully deleted product with ID: {ProductId}", id);
    return Results.NoContent();
});

app.Run();

// DTO records
public record CreateProductRequest(string Name, string? Description, decimal Price);
public record UpdateProductRequest(string Name, string? Description, decimal Price);
