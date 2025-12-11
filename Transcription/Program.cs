using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Transcription.Interfaces;
using Transcription.Models;
using Transcription.Services;
using System;
using Transcription.Interfaces;
using Transcription.Models;
using Transcription;


var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<AssemblyAiOptions>(builder.Configuration.GetSection("AssemblyAI"));

// Named HttpClient with auth header and reasonable timeout
builder.Services.AddHttpClient("assemblyai", (sp, client) =>
{
    var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AssemblyAiOptions>>().Value;
    client.BaseAddress = new Uri(cfg.BaseUrl);
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("authorization", cfg.ApiKey);
    client.Timeout = TimeSpan.FromMinutes(10);
});

// Register service
builder.Services.AddScoped<ITranscriptionService, AssemblyAiService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthorization();

app.MapControllers();

app.Run();


