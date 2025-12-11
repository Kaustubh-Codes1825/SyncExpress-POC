using System;
using System.Net;

namespace Transcription.Exceptions
{
    public class ApiException : TranscriptionException
    {
        public HttpStatusCode StatusCode { get; }
        public ApiException(string message, HttpStatusCode statusCode) : base(message) { StatusCode = statusCode; }
        public ApiException(string message, HttpStatusCode statusCode, Exception inner) : base(message, inner) { StatusCode = statusCode; }
    }
}
