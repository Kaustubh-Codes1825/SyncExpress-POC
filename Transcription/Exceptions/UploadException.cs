using System;
using System.Net;

namespace Transcription.Exceptions
{
    public class UploadException : TranscriptionException
    {
        public HttpStatusCode? StatusCode { get; }
        public UploadException(string message, HttpStatusCode? statusCode = null) : base(message) { StatusCode = statusCode; }
        public UploadException(string message, Exception inner, HttpStatusCode? statusCode = null) : base(message, inner) { StatusCode = statusCode; }
    }
}
