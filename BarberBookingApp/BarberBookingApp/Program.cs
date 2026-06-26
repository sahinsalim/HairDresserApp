using System.Globalization;
using BarberBookingApp.Components;
using BarberBookingApp.Data;
using BarberBookingApp.Endpoints;
using BarberBookingApp.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Twilio;

// Müşteriler Türk olduğu için tarih/saat metinleri (ay, gün adları) Türkçe gösterilir.
var turkishCulture = new CultureInfo("tr-TR");
CultureInfo.DefaultThreadCurrentCulture = turkishCulture;
CultureInfo.DefaultThreadCurrentUICulture = turkishCulture;

var builder = WebApplication.CreateBuilder(args);

// Render/Docker gibi container ortamlarinda PORT env degiskeni ile dinleme adresi atanir.
var containerPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(containerPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{containerPort}");
}

var twilioAccountSid = builder.Configuration["Twilio:AccountSid"];
var twilioAuthToken = builder.Configuration["Twilio:AuthToken"];
if (!string.IsNullOrWhiteSpace(twilioAccountSid) && !string.IsNullOrWhiteSpace(twilioAuthToken))
{
    TwilioClient.Init(twilioAccountSid, twilioAuthToken);
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database:Provider "Sqlite" (local demo), "Postgres" (Render/Neon/Supabase gibi canlı ortamlar)
// veya "SqlServer" olarak ayarlanabilir.
var dbProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var useSqlServer = string.Equals(dbProvider, "SqlServer", StringComparison.OrdinalIgnoreCase);
var usePostgres = string.Equals(dbProvider, "Postgres", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(dbProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (useSqlServer)
    {
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
    else if (usePostgres)
    {
        options.UseNpgsql(GetPostgresConnectionString(builder.Configuration));
    }
    else
    {
        var sqliteFile = builder.Configuration["Database:SqliteFile"] ?? "App_Data/berberarif.db";
        var sqlitePath = Path.Combine(builder.Environment.ContentRootPath, sqliteFile);
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);
        options.UseSqlite($"Data Source={sqlitePath}");
    }
});

builder.Services.AddHttpClient();

builder.Services.AddScoped<ISmsService, NetgsmSmsService>();
builder.Services.AddScoped<IOtpService, TwilioOtpService>();
builder.Services.AddScoped<IAppointmentService, AppointmentService>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/giris";
        options.AccessDeniedPath = "/giris";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;

        options.Events.OnRedirectToLogin = context =>
        {
            // Admin alanına yetkisiz erişim admin girişine, diğerleri müşteri girişine yönlendirilir.
            var isAdminRoute = context.Request.Path.StartsWithSegments("/admin");
            var loginPath = isAdminRoute ? "/admin/giris" : "/giris";
            var returnUrl = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
            context.Response.Redirect($"{loginPath}?returnUrl={returnUrl}");
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("CustomerOnly", policy => policy.RequireRole("Customer"));
});

builder.Services.AddCors();

// Render/Docker gibi reverse-proxy arkasinda calisirken X-Forwarded-Proto basligina
// guvenmezsek UseHttpsRedirection sonsuz yonlendirme dongusune girer.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (useSqlServer)
    {
        db.Database.Migrate();
    }
    else
    {
        // Sqlite/Postgres demo modu: şema doğrudan güncel modelden oluşturulur.
        db.Database.EnsureCreated();

        if (!usePostgres)
        {
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "AppointmentServiceItems" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AppointmentServiceItems" PRIMARY KEY AUTOINCREMENT,
                    "AppointmentId" INTEGER NOT NULL,
                    "ServiceTypeId" INTEGER NOT NULL,
                    CONSTRAINT "FK_AppointmentServiceItems_Appointments_AppointmentId" FOREIGN KEY ("AppointmentId") REFERENCES "Appointments" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_AppointmentServiceItems_ServiceTypes_ServiceTypeId" FOREIGN KEY ("ServiceTypeId") REFERENCES "ServiceTypes" ("Id") ON DELETE RESTRICT
                );
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppointmentServiceItems_AppointmentId_ServiceTypeId"
                ON "AppointmentServiceItems" ("AppointmentId", "ServiceTypeId");
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE INDEX IF NOT EXISTS "IX_AppointmentServiceItems_ServiceTypeId"
                ON "AppointmentServiceItems" ("ServiceTypeId");
                """);
        }
    }
    SeedData.Initialize(db);
}

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapAuthEndpoints();

app.UseStaticFiles();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string GetPostgresConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("Postgres")
                           ?? configuration.GetConnectionString("DefaultConnection")
                           ?? configuration["DATABASE_URL"];

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Postgres için ConnectionStrings:Postgres veya DATABASE_URL environment variable tanımlanmalıdır.");
    }

    if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return connectionString;
    }

    var uri = new Uri(connectionString);
    var userInfo = uri.UserInfo.Split(':', 2);
    var npgsqlBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
        SslMode = SslMode.Require
    };

    return npgsqlBuilder.ConnectionString;
}
