using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

internal class Program{

    public const int number_nodes = 3;
    public static List<TcpClient> other_nodes_addresses = new();
    public static List<Message> ReceivedMessages = new();
    public static string[] selfAddress = new string[2];
    
    private static readonly object lockObject = new();

    static void Main(string[] args) {
        if(args.Length != 1) {
            Console.WriteLine("Usage: dotnet run <node_id>");
            return;
        }
       
        int node_id = int.Parse(args[0]);
        Console.WriteLine($"Node ID: {node_id}");
        string line = File.ReadLines("ips.txt").Skip(node_id-1).Take(1).First();
        Console.WriteLine($"Node IP: {node_id} -> {line}");    
         // Create a Random object with the seed "42"
        int seed = 42;
        Random random = new(seed);

        selfAddress = line.Split(":");
        
        // Start a thread for the listener
        TcpListener listener = new(IPAddress.Parse(selfAddress[0]), int.Parse(selfAddress[1]));
        Thread listenerThread = new(() => StartListener(listener));
        listenerThread.Start();

        Thread.Sleep(10_000);
        //add other node connections
        for(int i = 1; i <= number_nodes; i++){
            if(i.Equals(node_id)){
                continue;
            }    
            string others = File.ReadLines("ips.txt").Skip(i-1).Take(1).First();
            string[] addresses = others.Split(":");
            TcpClient client = new();
            client.Connect(IPAddress.Parse(addresses[0]), int.Parse(addresses[1]));
            other_nodes_addresses.Add(client);
            Console.WriteLine($"Connected to the target node at {addresses[0]}:{addresses[1]}");
        }
        
        while(true) {
            Thread.Sleep(10000);
            // Generate random number with given seed
            int randomNumber = random.Next(number_nodes + 1) + 1;
            Console.WriteLine($"Leader is: {randomNumber}");
            if(node_id.Equals(randomNumber)) { // If node is leader
                Message messageWithBlock = new() {
                    MessageType = Type.Propose,
                    Content = new Content { Block = new Block {
                    Hash = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 }),
                    Epoch = node_id,
                    Length = node_id,
                Transactions = { new Transaction {
                    Sender = node_id,
                    Receiver = node_id,
                    Id = node_id,
                    Amount = 500.0}}
                }},
                    Sender = node_id
                };
                URB_Broadcast(messageWithBlock);
            }
        }
       
    }

    static void StartListener(TcpListener listener) {
        listener.Start();
        Console.WriteLine("Listener IP:" + selfAddress[0] + " PORT:" + selfAddress[1] + " node is waiting for incoming connections...");

        while (true) {
            TcpClient client = listener.AcceptTcpClient();
            // Handle the incoming connection in a new thread or task
            Task.Factory.StartNew(() => HandleConnection(client));
        }
    }

    static void HandleConnection(TcpClient client) {
        NetworkStream stream = client.GetStream();
        while (true) {
            byte[] data = new byte[1024]; 
            int bytesRead = stream.Read(data, 0, data.Length);

            if (bytesRead == 0) {
                continue;
            }

            
            Message? receivedMessage = DeserializeMessage(data, bytesRead);
            if (receivedMessage != null) {
                lock(lockObject) {
                    if (!IsMessageReceived(receivedMessage)) {
                        Console.WriteLine("Message Received: " + receivedMessage); // Adjust to display message content
                        AddReceivedMessage(receivedMessage);
                        // Echo message to the other nodes
                        Echo(receivedMessage);
                    }
                }
            }
        }
    }


   public static void URB_Broadcast(Message message) {
        // Serialize the message to bytes
        byte[] serializedMessage = SerializeMessage(message);
        // Send the serialized message
        TcpClient client = new();
        client.Connect(IPAddress.Parse(selfAddress[0]), int.Parse(selfAddress[1]));
        Console.WriteLine("Broadcast Message: " + message);
        NetworkStream networkStream = client.GetStream();
        networkStream.Write(serializedMessage, 0, serializedMessage.Length);
        networkStream.Close();
        client.Close();
}

    public static void Echo(Message message){
         // Serialize the message to bytes
        byte[] serializedMessage = SerializeMessage(message);
        foreach (TcpClient otherClient in other_nodes_addresses) {
            NetworkStream otherStream = otherClient.GetStream();
            // Send only the relevant bytes received, not the entire buffer
            otherStream.Write(serializedMessage, 0, serializedMessage.Length);
        }
    }

    // Function to serialize a Message object into bytes
    public static byte[] SerializeMessage(Message message) {
        using MemoryStream stream = new();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    // Function to deserialize bytes into a Message object
    public static Message? DeserializeMessage(byte[] data, int length) {
        try {
            Message message = new();
            message.MergeFrom(data, 0, length);
            return message;
        } catch (InvalidProtocolBufferException ex) {
            Console.WriteLine("Error deserializing message: " + ex.Message);
            return null;
        }
    }

    public static bool IsMessageReceived(Message message) {
        return ReceivedMessages.Contains(message);
    }

    public static void AddReceivedMessage(Message message) {
        ReceivedMessages.Add(message);
    }
}
