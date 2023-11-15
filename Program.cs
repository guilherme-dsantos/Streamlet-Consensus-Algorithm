using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

internal class Program{

    public const int NrNodes = 4;
    public static List<TcpClient> OtherAddresses = new();
    public static List<Message> ReceivedMessages = new();
    public static string[] SelfAddress = new string[2];

    public static int NrEpoch = 0;
    public static int DeltaEpoch = 10_000;
    public static List<Block> BlockChain = new();
    
    private static readonly object Lock = new();

    static void Main(string[] args) {
        
        // Input Validations
        if(args.Length != 1) {
            Console.Error.WriteLine("Usage: dotnet run <node_id>");
            return;
        }

        if(args[0] != "1" && args[0] != "2" && args[0] != "3" && args[0] != "4") {
            Console.Error.WriteLine("Usage: dotnet run <node_id>, <node_id> = 1, 2, 3 or 4");
            return;
        }

       
        int IdNode = int.Parse(args[0]);
        //Console.WriteLine($"Node ID: {IdNode}");
        string AdressLine = File.ReadLines("ips.txt").Skip(IdNode-1).Take(1).First();  
         // Create a Random object with the seed "42"
        int seed = 1;
        Random random = new(seed);

        SelfAddress = AdressLine.Split(":");
        
        // Start a thread for the listener
        TcpListener listener = new(IPAddress.Parse(SelfAddress[0]), int.Parse(SelfAddress[1]));
        Thread listenerThread = new(() => StartListener(listener));
        listenerThread.Start();
        Console.WriteLine("Waiting 10 seconds for other nodes to start listening...");
        Thread.Sleep(10_000);
        //add other node connections
        for(int i = 1; i <= NrNodes; i++){
            if(i.Equals(IdNode)){
                continue;
            }    
            string others = File.ReadLines("ips.txt").Skip(i-1).Take(1).First();
            string[] addresses = others.Split(":");
            TcpClient client = new();
            client.Connect(IPAddress.Parse(addresses[0]), int.Parse(addresses[1]));
            OtherAddresses.Add(client);
            //Console.WriteLine($"Connected to the target node at {addresses[0]}:{addresses[1]}");
        }
        
        while(true) {
             // Generate random number with given seed
            int Leader = random.Next(NrNodes) + 1;

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Epoch: {++NrEpoch} started with Leader {Leader}");
            Console.ResetColor();
           
            //Console.WriteLine($"Leader is: {randomNumber}");
            if(IsLeader(IdNode,Leader)) { // If node is leader
                Message ProposeMessage = ProposeBlock(IdNode);
                URB_Broadcast(ProposeMessage);
            }

            Thread.Sleep(DeltaEpoch);
        }
       
    }

    static void StartListener(TcpListener listener) {
        listener.Start();
        //Console.WriteLine("Listener IP:" + SelfAddress[0] + " PORT:" + SelfAddress[1] + " node is waiting for incoming connections...");

        while (true) {
            TcpClient client = listener.AcceptTcpClient();
            // Handle the incoming connection in task
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
                lock(Lock) {
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
        client.Connect(IPAddress.Parse(SelfAddress[0]), int.Parse(SelfAddress[1]));
        Console.WriteLine("Broadcast Message: " + message);
        NetworkStream networkStream = client.GetStream();
        networkStream.Write(serializedMessage, 0, serializedMessage.Length);
        networkStream.Close();
        client.Close();
}

    public static void Echo(Message message){
         // Serialize the message to bytes
        byte[] serializedMessage = SerializeMessage(message);
        foreach (TcpClient otherClient in OtherAddresses) {
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
            Console.Error.WriteLine("Error deserializing message: " + ex.Message);
            return null;
        }
    }

    public static bool IsMessageReceived(Message message) {
        return ReceivedMessages.Contains(message);
    }

    public static void AddReceivedMessage(Message message) {
        ReceivedMessages.Add(message);
    }

    public static bool IsLeader(int node_id, int randomNumber){
        return node_id.Equals(randomNumber);
    }

    public static Message ProposeBlock(int node_id){
        Message messageWithBlock = new() {
            MessageType = Type.Propose,
            Content = new Content { 
                Block = new Block {
                    Hash = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 }),
                    Epoch = node_id,
                    Length = node_id,
                    Transactions = { new Transaction {
                        Sender = node_id,
                        Receiver = node_id,
                        Id = node_id,
                        Amount = 500.0}
                    }
                }
            },
            Sender = node_id
        };
        return messageWithBlock;
    }
}
