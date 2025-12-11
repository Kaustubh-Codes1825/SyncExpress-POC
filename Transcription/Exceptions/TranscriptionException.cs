using System;

namespace Transcription.Exceptions
{
    public class TranscriptionException : Exception
    {
        public TranscriptionException(string message) : base(message) { }
        public TranscriptionException(string message, Exception inner) : base(message, inner) { }
    }
}
