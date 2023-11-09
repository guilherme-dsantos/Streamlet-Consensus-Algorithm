public enum Type {
    Propose,
    Vote,
    Echo
}

public class Message {
    public required Type message_type;
    public required object Content;
    public required int Sender;

}