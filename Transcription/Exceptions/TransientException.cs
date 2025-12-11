using System;

namespace Transcription.Exceptions
{
    public class TransientException : TranscriptionException
    {
        public TransientException(string message) : base(message) { }
        public TransientException(string message, Exception inner) : base(message, inner) { }
    }
}
