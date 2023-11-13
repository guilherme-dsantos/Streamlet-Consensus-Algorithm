using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;


internal class Program{

    public const int number_nodes = 3;
    public static List<TcpClient> other_nodes_addresses = new();
    public static List<Message> ReceivedMessages = new();
    public static string[] selfAddress = new string[2];

    static void Main(string[] args) {
        if(args.Length != 1) {
            Console.WriteLine("Usage: dotnet run <node_id>");
            return;
        }
       
        int node_id = int.Parse(args[0]);
        Console.WriteLine($"Node ID: {node_id}");
        string line = File.ReadLines("ips.txt").Skip(node_id).Take(1).First();
              
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
        for(int i = 0; i < number_nodes; i++){
            if(i.Equals(node_id)){
                continue;
            }    
            string others = File.ReadLines("ips.txt").Skip(i).Take(1).First();
            string[] addresses = others.Split(":");
            TcpClient client = new();
            client.Connect(IPAddress.Parse(addresses[0]), int.Parse(addresses[1]));
            other_nodes_addresses.Add(client);
            Console.WriteLine($"Connected to the target node at {addresses[0]}:{addresses[1]}");
        }

        Message messageWithBlock = new Message
{
    MessageType = Type.Vote,
    Content = new Content { Block = new Block
    {
        Hash = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 }),
        Epoch = 123,
        Length = 10,
        Transactions = { new Transaction
        {
            Sender = 1,
            Receiver = 2,
            Id = 123,
            Amount = 500.0
        }}
    }},
    Sender = 789
};

        
    
        while(true) {
            Thread.Sleep(10000);
            // Generate random number with given seed
            int randomNumber = random.Next(number_nodes);
            Console.WriteLine($"Leader is: {randomNumber}");
    
            if(node_id.Equals(randomNumber)) { // If node is leader
                Console.WriteLine("Im the leader");
                URB_Broadcast(messageWithBlock);
            }
        }
       
    }

    static void StartListener(TcpListener listener) {
        listener.Start();
        Console.WriteLine("Listener " + listener.ToString() + " node is waiting for incoming connections...");

        while (true) {
            TcpClient client = listener.AcceptTcpClient();
            // Handle the incoming connection in a new thread or task
            Task.Factory.StartNew(() => HandleConnection(client));
        }
    }

    static void HandleConnection(TcpClient client) {
        Console.WriteLine("One node connected");
        NetworkStream stream = client.GetStream();

        while (true) {
            byte[] data = new byte[1024]; 

        int bytesRead = stream.Read(data, 0, data.Length);

        if (bytesRead == 0) {
            continue;
        }

        Message? receivedMessage = DeserializeMessage(data, bytesRead);

        if (receivedMessage != null) {
            if (!IsMessageReceived(receivedMessage)) {
                Console.WriteLine("Message: " + receivedMessage); // Adjust to display message content
                AddReceivedMessage(receivedMessage);

                // Broadcast the received message to other nodes
                Echo(receivedMessage);
            } else {
                Console.WriteLine("Message already received");
            }
        }
    }
    }


   public static void URB_Broadcast(Message message) {
        // Serialize the message to bytes
        byte[] serializedMessage = SerializeMessage(message);

        // Send the serialized message
        TcpClient client = new TcpClient();
        client.Connect(IPAddress.Parse(selfAddress[0]), int.Parse(selfAddress[1]));
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
    using (MemoryStream stream = new MemoryStream()) {
        message.WriteTo(stream);
        return stream.ToArray();
    }
}

// Function to deserialize bytes into a Message object
public static Message? DeserializeMessage(byte[] data, int length) {
    try {
        Message message = new Message();
        message.MergeFrom(data, 0, length);
        return message;
    } catch (InvalidProtocolBufferException ex) {
        Console.WriteLine("Error deserializing message: " + ex.Message);
        return null;
    }
}

public static bool IsMessageReceived(Message message) {
    // Add your logic to check if the message is already in the ReceivedMessages list
    return ReceivedMessages.Contains(message);
}

public static void AddReceivedMessage(Message message) {
    // Add your logic to add the message to the ReceivedMessages list
    ReceivedMessages.Add(message);
}
}
