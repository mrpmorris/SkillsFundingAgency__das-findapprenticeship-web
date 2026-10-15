using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SFA.DAS.FAA.Web.AppStart;
using SFA.DAS.FAA.Web.ModelBinding;
using SFA.DAS.FAA.Web.Filters;
using SFA.DAS.FAA.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var isIntegrationTest = builder.Environment.EnvironmentName.Equals("IntegrationTest", StringComparison.CurrentCultureIgnoreCase);
var rootConfiguration = builder.Configuration.LoadConfiguration(isIntegrationTest);

builder.Services
    .AddOptions()
    .AddMemoryCache()
    .AddValidatorsFromAssembly(typeof(Program).Assembly)
    .AddControllers(options =>
    {
        options.ModelBinderProviders.Insert(0, new MonthYearDateModelBinderProvider());
        options.ModelBinderProviders.Insert(0, new DayMonthYearDateModelBinderProvider());
    });

builder.Services.AddConfigurationOptions(rootConfiguration);

builder.Services.AddLogging();
builder.Services.Configure<IISServerOptions>(options => { options.AutomaticAuthentication = false; });
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.CheckConsentNeeded = context => true;
    options.MinimumSameSitePolicy = SameSiteMode.None;
});
builder.Services.AddServiceRegistration(isIntegrationTest);
builder.Services.AddAuthenticationServices(rootConfiguration);
builder.Services.AddCacheServices(rootConfiguration);
builder.Services.AddHealthChecks()
    .AddCheck<AzureKeyVaultSecretHealthCheck>("KeyVaultSecret", failureStatus: HealthStatus.Unhealthy)
    .AddCheck<SearchIndexStatisticsHealthCheck>("AzureSearchIndex", failureStatus: HealthStatus.Unhealthy);

builder.Services.AddControllersWithViews()
    .AddSessionStateTempDataProvider();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("Search", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{context.Request.Path}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromSeconds(15);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.Configure<RouteOptions>(_ =>
{

}).AddMvc(options =>
{
    if (!isIntegrationTest)
    {
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    }

    options.Filters.Add(new SignInLinkFilter());

    options.Filters.Add(new NewFaaUserAccountFilter());
});

builder.Services.AddTransient<IStartupFilter,
    RequestSetOptionsStartupFilter>();

builder.Services.AddDataProtection(rootConfiguration);
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddExceptionHandler<ResourceNotFoundExceptionHandler>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error/500");
}


app.UseHealthChecks();

app.UseAuthentication();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();

app.AddRedirectRules();
app.UseStaticFiles();
app.UseCookiePolicy();

app.UseSession();

app.UseEndpoints(endpointBuilder =>
{
    endpointBuilder?.MapControllerRoute(
        name: "default",
        pattern: "{controller=SearchApprenticeshipsController}/{action=Index}/{id?}");
});

app.Run();