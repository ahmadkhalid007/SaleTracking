using Microsoft.AspNetCore.Authentication.Cookies;
using SaleTracking.Services;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment() &&
    DevelopmentStartup.FindPortConflict(builder.Configuration["urls"]) is { } conflict)
{
    Console.Error.WriteLine(conflict);
    Environment.ExitCode = 1;
    return;
}
builder.Services.AddSingleton(new TrackingSchedule(TimeZoneInfo.FindSystemTimeZoneById(
    builder.Configuration["Tracking:TimeZone"] ?? "Asia/Karachi")));
builder.Services.AddControllersWithViews();
builder.Services.AddSingleton(_ => new SqliteDatabase(Path.GetFullPath(
    builder.Configuration["Database:Path"] ?? "../.local/saletracking.db", builder.Environment.ContentRootPath)));
builder.Services.AddSingleton<SqliteAccountStore>();
builder.Services.AddSingleton<IAccountStore>(services => services.GetRequiredService<SqliteAccountStore>());
builder.Services.AddSingleton<IAdminAccountStore>(services => services.GetRequiredService<SqliteAccountStore>());
builder.Services.AddSingleton<ITrackingStore, SqliteTrackingStore>();
builder.Services.AddSingleton<IWhatsAppVerificationService, DevelopmentWhatsAppVerificationService>();
builder.Services.AddHttpClient<IPriceScraper, GenericHtmlPriceScraper>();
builder.Services.Configure<WhatsAppWebOptions>(builder.Configuration.GetSection("WhatsApp:Web"));
builder.Services.PostConfigure<WhatsAppWebOptions>(options =>
{
    options.TokenFile = Path.GetFullPath(options.TokenFile, builder.Environment.ContentRootPath);
    options.BridgeDirectory = Path.GetFullPath(options.BridgeDirectory, builder.Environment.ContentRootPath);
    options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
});
builder.Services.AddHttpClient<WhatsAppWebNotificationService>(client =>
    client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<IWhatsAppNotificationService>(services => services.GetRequiredService<WhatsAppWebNotificationService>());
builder.Services.AddTransient<IWhatsAppConnectionService>(services => services.GetRequiredService<WhatsAppWebNotificationService>());
builder.Services.AddHostedService<WhatsAppBridgeWorker>();
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<ISmtpConfigurationStore, SqliteSmtpConfigurationStore>();
builder.Services.AddSingleton<IEmailNotificationService, SmtpEmailNotificationService>();
builder.Services.AddSingleton<IInAppNotificationService, SqliteInAppNotificationService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PriceTrackingProcessor>();
builder.Services.AddHostedService<PriceTrackingWorker>();
builder.Services.AddScoped<AccountCookieEvents>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.EventsType = typeof(AccountCookieEvents);
});

var app = builder.Build();

// Schema creation and legacy admin import finish before requests or workers start.
app.Services.GetRequiredService<SqliteDatabase>().Initialize(Path.GetFullPath(
    builder.Configuration["Admin:AccountFile"] ?? "../.local/admin/account.json", builder.Environment.ContentRootPath));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("dashboard", "Dashboard/{action=Index}/{id?}", new { controller = "Dashboard", action = "Index" });
app.MapControllerRoute("settings", "Settings/{action=Index}/{id?}", new { controller = "Settings", action = "Index" });
app.MapControllerRoute("default", "{controller=Account}/{action=Login}/{id?}");

try { app.Run(); }
catch (IOException ex) when (DevelopmentStartup.IsAddressInUse(ex))
{
    Console.Error.WriteLine("SaleTracking could not start because its port is already in use. " +
        "Stop the existing SaleTracking instance before starting another, or choose another URL with --urls.");
    Environment.ExitCode = 1;
}
