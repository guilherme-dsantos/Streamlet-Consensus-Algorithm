using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Collections;

internal class Program{

    public const int TotalNodes = 4;

    public static int IDNode;
    public static List<TcpClient> OtherAddresses = new();
    public static List<Message> ReceivedMessages = new();
    public static string[] SelfAddress = new string[2];

    public static List<Block> BlocksProposed = new();

    public static int Epoch = 0;
    public static int DeltaEpoch = 5_000;
    public static List<Block> BlockChain = new();
    
    public static readonly object MessageLock = new();
    public static readonly object WriteLock = new();
    public volatile static CountdownEvent cde = new(TotalNodes - 1);
    public static int Leader;

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

       
        IDNode = int.Parse(args[0]);
        //Console.WriteLine($"Node ID: {IdNode}");
        string address_from_file = File.ReadLines("ips.txt").Skip(IDNode-1).Take(1).First();  
         // Create a Random object with the seed "42"

        //Genesis block
        BlockChain.Add(InitBlock());

        int seed = 9054;
        Random random = new(seed);

        SelfAddress = address_from_file.Split(":");
        
        // Start a thread for the listener
        TcpListener listener = new(IPAddress.Parse(SelfAddress[0]), int.Parse(SelfAddress[1]));
        Thread listenerThread = new(() => StartListener(listener));
        listenerThread.Start();
        Console.WriteLine("Waiting 10 seconds for other nodes to start listening...");
        Thread.Sleep(10_000);
        //add other node connections
        for(int i = 1; i <= TotalNodes; i++) {
            if(i.Equals(IDNode)){
                continue;
            }
            string others = File.ReadLines("ips.txt").Skip(i-1).Take(1).First();
            string[] addresses = others.Split(":");
            TcpClient client = new();
            client.Connect(IPAddress.Parse(addresses[0]), int.Parse(addresses[1]));
            OtherAddresses.Add(client);
            //Console.WriteLine($"Connected to the target node at {addresses[0]}:{addresses[1]}");
        }

        // Wait for all nodes to be ready
        cde.Wait();
        while(true) {
            
             // Generate random number with given seed
            Leader = random.Next(TotalNodes) + 1;

             //Print epoch number and leader to console in blue every time a new epoch starts
            lock(WriteLock) {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"Epoch: {++Epoch} started with Leader {Leader}");
                Console.ResetColor();
            }
            //Console.WriteLine($"Leader is: {randomNumber}");
            if(IsLeader()) { //if leader, propose block
                byte[] hash = ComputeParentHash();
                ProposeBlock(IDNode, hash, Epoch, BlockChain.Count, new List<Transaction>()); 
                //THE BLOCKCHAIN.COUNT IS NOT TRUE WITH FORKS (USE THE LAST FUNCTION MADE BUT RETURN THE AUX+1 AND NOT THE BLOCK)
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
        if (cde.CurrentCount > 0) {
            cde.Signal();
        }
        NetworkStream stream = client.GetStream();
        while (true) {
            
            byte[] data = new byte[1024]; 
            int bytesRead = stream.Read(data, 0, data.Length);
            if (bytesRead == 0) {
                continue;
            }
            
            Message? receivedMessage = DeserializeMessage(data, bytesRead);
            if (receivedMessage != null) {
                lock(MessageLock) {
                    if (!IsMessageReceived(receivedMessage)) {
                        lock(WriteLock){
                            Console.WriteLine("Message Received: " + receivedMessage); // Adjust to display message content
                        }
                        ReceivedMessages.Add(receivedMessage);
                        // Echo message to the other nodes
                        Echo(receivedMessage);
                        if(!IsLeader() && receivedMessage.MessageType == Type.Propose) { //other nodes vote for proposed block
                            VoteBlock(receivedMessage);
                        }
                        if(receivedMessage.MessageType == Type.Propose && !BlocksProposed.Contains(receivedMessage.Content.Block)){
                            BlocksProposed.Add(receivedMessage.Content.Block);
                        }
                        if(receivedMessage.MessageType == Type.Vote){
                            if(!BlocksProposed.Contains(receivedMessage.Content.Block)){
                                BlocksProposed.Add(receivedMessage.Content.Block);
                            }
                            foreach (Block item in BlocksProposed)
                            {
                                if(item.Equals(receivedMessage.Content.Block)){
                                    if(!item.Votesenders.Contains(receivedMessage.Sender))
                                        item.Votesenders.Add(receivedMessage.Sender);
                                    if(item.Votesenders.Count > TotalNodes/2  && !BlockChain.Contains(receivedMessage.Content.Block)){
                                        BlockChain.Add(receivedMessage.Content.Block);
                                        Console.WriteLine("Block Notarized: " + receivedMessage.Content.Block);
                                    }
                                        
                                }
                            }
                            

                        }
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

    public static void Echo(Message message) {
         // Serialize the message to bytes
        byte[] serializedMessage = SerializeMessage(message);
        foreach (TcpClient otherClient in OtherAddresses) {
            NetworkStream otherStream = otherClient.GetStream();
            // Send only the relevant bytes received, not the entire buffer
            otherStream.Write(serializedMessage, 0, serializedMessage.Length);
        }
    }

    public static Block InitBlock() {
        Block block = new() {
            Hash = ByteString.CopyFrom(new byte[] {0}),
            Epoch = 0,
            Length = 0,
            Votesenders = {new List<int>()},
            //Notarized=false;
            Transactions = { new Transaction {
                Sender = 0,
                Receiver = 0,
                Id = 0,
                Amount = 0.0
            }}
        };
        return block;
    }

    public static void ProposeBlock(int node_id, byte[] hash, int epoch, int length, List<Transaction> transactions){
        Message messageWithBlock = new() {
            MessageType = Type.Propose,
            Content = new Content { 
                Block = new Block {
                    Hash = ByteString.CopyFrom(hash), 
                    Epoch = epoch,
                    Length = length,
                    Votesenders = {new List<int>()},
                    Transactions = {transactions},
                }
            },
            Sender = node_id
        };
        URB_Broadcast(messageWithBlock);
    }

    public static void VoteBlock(Message proposedBlock){
        Block voteBlock = proposedBlock.Content.Block;
        voteBlock.Transactions.Clear();
        Message voteMessage = new() {
            MessageType = Type.Vote,
            Content = new Content { 
                Block = voteBlock
            },
            Sender = IDNode
        };
        URB_Broadcast(voteMessage);
    }

    public static byte[] ComputeParentHash() {
        Block lastBlock = BlockChain.Last();
        byte[] hash = lastBlock.Hash.ToByteArray();
        byte[] newHash = SHA1.HashData(hash);
        return newHash;
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

    public static bool IsLeader(){
        return IDNode.Equals(Leader);
    }

    public static int BlockToProposeAfter(){
        int aux=-1;
        foreach (Block item in BlockChain)
        {
            if(item.Length > aux){
                aux=item.Length;
            } 
        }
        return aux+1;
    } 
    
}
