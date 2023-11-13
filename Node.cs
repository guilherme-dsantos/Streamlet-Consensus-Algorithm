using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

public class Node {
    public int ID;

    public TcpListener Listener;

    public List<TcpClient> Connections = new();

    public List<Block> Blockchain = new();

    [SetsRequiredMembers]
    public Node(int id, TcpListener listener, List<TcpClient> connections){
        ID = id;
        Listener = listener;
        Connections = connections;
    }
}

  