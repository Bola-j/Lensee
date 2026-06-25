using System.Text;
using System.Text.Json;
using Lensee.Host.Endpoints;
using Lensee.Host.Infrastructure;
using Lensee.Modules.Catalog.Data;
using Lensee.Modules.Catalog.Services;
using Lensee.Modules.CRM.Data;
using Lensee.Modules.Identity.Data;
using Lensee.Modules.Inventory.Data;
using Lensee.Modules.Notifications.Data;
using Lensee.Modules.Operations.Data;
using Lensee.Modules.Payments.Data;
using Lensee.Modules.Reporting.Data;
using Lensee.SharedKernel.Abstractions;
using Lensee.SharedKernel.Data;
using Lensee.SharedKernel.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Lensee API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste a JWT access token."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? ["http://localhost:3000", "http://localhost:5173", "http://localhost:8080"];

    options.AddPolicy("Spa", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();

            return;
        }

        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<CatalogDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<CrmDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<OperationsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<NotificationsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<ReportingDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContext<SharedDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddScoped<IClock, SystemClock>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IAuditLogWriter, AuditLogWriter>();
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<CategoryTreeService>();
builder.Services.AddScoped<SkuCodeGenerator>();
builder.Services.AddScoped<ICatalogEventPublisher, NoOpCatalogEventPublisher>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql");

var jwtSecret = builder.Configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "Lensee";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "Lensee.App";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("users.read", policy => policy.RequireClaim("permission", LenseePermissions.UsersRead));
    options.AddPolicy("users.write", policy => policy.RequireClaim("permission", LenseePermissions.UsersWrite));
    options.AddPolicy("catalog.read", policy => policy.RequireClaim("permission", LenseePermissions.CatalogRead));
    options.AddPolicy("catalog.write", policy => policy.RequireClaim("permission", LenseePermissions.CatalogWrite));
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var problem = new ProblemDetails
        {
            Title = "An unexpected error occurred.",
            Detail = app.Environment.IsDevelopment() ? exception?.Message : null,
            Status = StatusCodes.Status500InternalServerError,
            Instance = context.Request.Path
        };

        context.Response.StatusCode = problem.Status.Value;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
    });
});


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Spa");
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthResponseAsync });
app.MapHealthChecks("/api/v1/health", new HealthCheckOptions { ResponseWriter = WriteHealthResponseAsync });

app.MapGet("/api/v1", () => Results.Ok(new
{
    name = "Lensee API",
    version = "v1",
    serverTime = DateTimeOffset.UtcNow
}))
.AllowAnonymous()
.WithTags("Platform");

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapCatalogEndpoints();

app.Run();

static Task WriteHealthResponseAsync(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = "application/json";

    var payload = new
    {
        status = report.Status.ToString(),
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            description = entry.Value.Description
        })
    };

    return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

public partial class Program;
