using Transcription.Models;
using Transcription.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<AssemblyAiOptions>(
    builder.Configuration.GetSection("AssemblyAI"));


builder.Services.AddHttpClient<AssemblyAIService>();
builder.Services.AddLogging();


builder.Services.AddHttpClient();
builder.Services.AddScoped<AssemblyAIService>();
builder.Services.AddScoped<WordTranscriptionService>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
    });

// allow requests from your React dev server (Vite default)
builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalDev", policy =>
    {
        policy.WithOrigins("http://localhost:5173") // React/Vite dev URL
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});




var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("LocalDev");

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
