using System.Net;
using System.Net.Sockets;

public class Node {
    public required int ID;

    public required TcpListener Listener;

    public List<TcpClient> Connections = new List<TcpClient>();

    public List<Block> Blockchain = new List<Block>();

    public Node(int id, TcpListener listener){
        ID = id;
        Listener = listener;
    }
}

  