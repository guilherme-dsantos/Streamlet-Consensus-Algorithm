public class Block {
    public required byte[] Hash {set; get;}
    public required int Epoch {set; get;}
    public required int Length {set; get;}
    public List<Transaction> transactions { get; set; } = new List<Transaction>();

}

