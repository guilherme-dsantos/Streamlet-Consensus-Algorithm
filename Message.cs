public enum Type {
    Propose,
    Vote,
    Echo
}

public class Message {
    public required Type message_type;
    public required Content Content;
    public required int Sender;

}

public struct Content
{
    public string Text { get; set; }
    public Block Block { get; set; }
}