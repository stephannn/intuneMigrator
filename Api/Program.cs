using Azure.Identity;
using intuneMigratorApi.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Identity.Web;
using System.Security.Claims;

var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddMemoryCache();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
    ; // End chain here. We do not use the user token for downstream Graph calls.

// Register GraphServiceClient for the separate Intune Tenant using Client Credentials
builder.Services.AddKeyedScoped<GraphServiceClient>("Source", (sp, key) =>
{
    var config = sp.GetRequiredService<IConfiguration>().GetSection("IntuneSourceTenantConfig");
    var credential = new ClientSecretCredential(config["TenantId"], config["ClientId"], config["ClientSecret"]);
    // .default scope is required for client credentials flow
    return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
});

builder.Services.AddKeyedScoped<GraphServiceClient>("Destination", (sp, key) =>
{
    var config = sp.GetRequiredService<IConfiguration>().GetSection("IntuneDestinationTenantConfig");
    var credential = new ClientSecretCredential(config["TenantId"], config["ClientId"], config["ClientSecret"]);
    // .default scope is required for client credentials flow
    return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
});

var dbProvider = builder.Configuration.GetSection("Database");

builder.Services.AddDbContext<IntuneMigratorDBContext>(options =>
{
    if ((dbProvider.GetValue<string>("DatabaseProvider") ?? "").Equals("SQLite", StringComparison.OrdinalIgnoreCase))
    {
        var sqliteConn = dbProvider.GetConnectionString("SQLiteConnection");
        options.UseSqlite(sqliteConn);
    }
    else
    {
        var sqlConn =dbProvider.GetConnectionString("MsSQLConnection");
        options.UseSqlServer(sqlConn, providerOptions => providerOptions.EnableRetryOnFailure());
    }
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();

if (app.Environment.IsDevelopment() && false)
{
    app.Use(async (context, next) =>
    {
        if (context.User.Identity is not { IsAuthenticated: true })
        {
            var config = context.RequestServices.GetService<IConfiguration>();
            var scopes = config?.GetValue<string>("AzureAd:Scopes") ?? "access_as_user";

            var claims = new[] { 
                new Claim(ClaimTypes.Name, "DevUser"),
                new Claim("scp", scopes),
                new Claim("http://schemas.microsoft.com/identity/claims/scope", scopes)
            };
            var identity = new ClaimsIdentity(claims, "DevAuth");
            context.User = new ClaimsPrincipal(identity);
        }
        await next();
    });
}

app.UseAuthorization();

app.MapControllers();

app.Run();
