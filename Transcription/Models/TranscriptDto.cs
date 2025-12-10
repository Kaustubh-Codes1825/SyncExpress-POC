namespace Transcription.Models;

public class SimpleTranscriptDto
{
    public string FullText { get; set; } = "";
    public List<SentenceDto> Sentences { get; set; } = new();
}

public class SimpleUtterance
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
}

public class SentenceDto
{
    public string Speaker { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    // Original raw values (if you want to keep them)
    //public int Start { get; set; }  
    //public int End { get; set; }     

    // New formatted fields
    public string StartTime { get; set; } = "";   
    public string EndTime { get; set; } = "";     

    //public float? Confidence { get; set; }        
    public string? ConfidencePercent { get; set; } 
}
public class WordDto
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public int Start { get; set; }     
    public int End { get; set; }       
    public float Confidence { get; set; }
}