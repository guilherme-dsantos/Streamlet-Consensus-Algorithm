using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;

internal class Program{

    // Network Configuration
    public const int TotalNodes = 5;
    public static int IDNode;
    public static List<TcpClient> OtherAddresses = new();
    public static List<Message> ReceivedMessages = new();
    public static volatile int Votes = 0;
    public static string[] SelfAddress = new string[2];
    public static NetworkStream? MyselfStream;

    // Consensus and Voting
    public static bool VotedInEpoch; // Nodes can only vote at most once on each epoch
    public static int Epoch = 0;
    public static int DeltaEpoch = 10_000;
    public static Block ProposedBlock = new();

    // Blockchain
    public static List<Block> BlockChain = new();

    // Thread Synchronization
    public static readonly object MessageLock = new();
    public volatile static CountdownEvent cde = new(TotalNodes);

    // Leader Election
    public static int Leader;

    // Pointer for Last Finalized Block
    public static int PointerLastFinalized = 0;


    static void Main(string[] args) {
        
        // Input Validations
        if(args.Length != 1) {
            Console.Error.WriteLine("Usage: dotnet run <node_id>");
            return;
        }
        if (! new string[] { "1", "2", "3", "4", "5" }.Contains(args[0])) {
            Console.Error.WriteLine("Usage: dotnet run <node_id>, <node_id> = [1;5]");
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
            Console.WriteLine($"Connecting to {others}");
            string[] addresses = others.Split(":");
            TcpClient client = new();
            client.Connect(IPAddress.Parse(addresses[0]), int.Parse(addresses[1]));
            OtherAddresses.Add(client);
        }

        TcpClient myself = new();
        myself.Connect(IPAddress.Parse(SelfAddress[0]), int.Parse(SelfAddress[1]));
        MyselfStream = myself.GetStream();

        // Wait for all nodes to be ready
cde.Wait();
while(true) {
    ReceivedMessages.Clear();
    //Console.WriteLine($"Epoch {Epoch} started");
    VotedInEpoch = false; //Sets voted to false at the start of each epoch
    ++Epoch;
    // Generate random number with given seed
    Leader = random.Next(TotalNodes) + 1;

    //if leader, propose block
    if(IsLeader()) { 
        byte[] hash = ComputeParentHash();
        
        List<Transaction> transactions = new();
        Transaction myTransaction = new()
        {
            Sender = 123,
            Receiver = 456,
            Id = 789,
            Amount = 5.75
        };
        transactions.Add(myTransaction);

        ProposeBlock(IDNode, hash, Epoch, BlockChain.Last().Length + 1, transactions);
    }
    Thread.Sleep(DeltaEpoch);
}

       
    }

    static void StartListener(TcpListener listener) {
        listener.Start();
        for(int i = 0; i < TotalNodes; i++) {
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
            
            byte[] data = new byte[2048]; 
            int bytesRead = stream.Read(data, 0, data.Length);
            if (bytesRead == 0) {
                continue;
            }
            
            Message? receivedMessage = DeserializeMessage(data, bytesRead);
            if (receivedMessage != null) {
                lock(MessageLock) {
                    if (!IsMessageReceived(receivedMessage)) {
                        if(receivedMessage.MessageType == Type.Echo) {
                            ReceivedMessages.Add(receivedMessage.Content.Message);
                        }
                        else {
                            ReceivedMessages.Add(receivedMessage);
                        }
                        Thread.Sleep(100);
                        //Console.WriteLine($"Received message {receivedMessage}");
                        if(receivedMessage.MessageType == Type.Echo) {
                            Echo(receivedMessage.Content.Message);
                            //Console.WriteLine($"Echoing echo message from {receivedMessage.Sender} with {receivedMessage.Content.Message.Content}");
                        } else {
                            //Console.WriteLine($"Echoing no echo message from {receivedMessage.Sender} with {receivedMessage}");
                            Echo(receivedMessage);
                            //Console.WriteLine($"Echoing message from {receivedMessage.Sender} with {receivedMessage.Content}");
                        }

                        if(receivedMessage.MessageType == Type.Propose || (receivedMessage.MessageType == Type.Echo && receivedMessage.Content.Message.MessageType == Type.Propose)) {
                            ProposedBlock = receivedMessage.MessageType == Type.Propose ? receivedMessage.Content.Block : receivedMessage.Content.Message.Content.Block;
                            Interlocked.Exchange(ref Votes,0);
                            if(receivedMessage.MessageType == Type.Echo && !VotedInEpoch && IsBlockValid(receivedMessage.Content.Message)) {
                                BroadcastVotes(receivedMessage);
                                VotedInEpoch = true;
                            }
                        }

                        if(receivedMessage.MessageType == Type.Vote || (receivedMessage.MessageType == Type.Echo && receivedMessage.Content.Message.MessageType == Type.Vote)) {
                            Interlocked.Increment(ref Votes);
                            if(Votes > TotalNodes / 2) {
                                //Console.WriteLine($"Block {receivedMessage.Content.Block} has been finalized");
                                //Console.Write(FindBlock(receivedMessage));
                                BlockChain.Add(ProposedBlock);
                                Interlocked.Exchange(ref Votes,0);
                                CheckFinalizationCriteria();
                                ProposedBlock = new();
                                foreach(Block b in BlockChain) {
                                    Console.WriteLine(b);
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
        // Send the serialized message to myself
        MyselfStream?.Write(serializedMessage, 0, serializedMessage.Length);
    }

    public static void Echo(Message message) {
        Message echoMessage = new() {
            MessageType = Type.Echo,
            Content = new Content { 
                Message = message
            },
            Sender = IDNode
        };
         // Serialize the message to bytes
        byte[] serializedMessage = SerializeMessage(echoMessage);
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
            Transactions = {}
        };
        return block;
    }

    public static void ProposeBlock(int node_id, byte[] hash, int epoch, int length, List<Transaction> transactions){
       Message messageWithBlock = new()
    {
        MessageType = Type.Propose,
        Content = new Content
        {
            Block = new Block
            {
                Hash = ByteString.CopyFrom(hash),
                Epoch = epoch,
                Length = length,
                Transactions = { transactions } // Initialize as a new list
            }
        },
        Sender = node_id
    };

    //Console.WriteLine($"Proposing block {messageWithBlock}");
    URB_Broadcast(messageWithBlock);
    }

    public static void CheckFinalizationCriteria(){
        if(BlockChain.Count < 3) {
            return;
        }
        List<Block> lastBlocks = BlockChain.GetRange(BlockChain.Count - 3, 3);
        if(lastBlocks[0].Epoch == lastBlocks[1].Epoch - 1 && lastBlocks[1].Epoch == lastBlocks[2].Epoch - 1 &&
           lastBlocks[0].Length == lastBlocks[1].Length - 1 && lastBlocks[1].Length == lastBlocks[2].Length - 1) {
            PointerLastFinalized = lastBlocks[1].Length;
        }
    }

    public static void BroadcastVotes(Message messageWithBlock){
        Block voteBlock = messageWithBlock.Content.Message.Content.Block;
        //Block voteBlock = proposedBlock.Content.Block;
        //voteBlock.Transactions.Clear();
        Message voteMessage = new() {
            MessageType = Type.Vote,
            Content = new Content { 
                Block = voteBlock
            },
        Sender = IDNode
        };
        URB_Broadcast(voteMessage);
    }

    public static bool IsBlockValid(Message message) {
        Block block = message.Content.Block;
        int maxId = BlockChain.Max(block => block.Epoch);
        if(block.Length <= PointerLastFinalized) {
            Console.WriteLine("Block length is not valid");
            return false;
        }
        if(block.Epoch <= maxId) {
            Console.WriteLine("Block epoch is not valid");
            return false;
        }
        return true;
    }

    public static Block FindBlock(Message mensaje){
        Message found = new();
        if(mensaje.MessageType == Type.Echo) {
            ReceivedMessages.Find(found => mensaje.Content.Message.Content.Block.Hash.Equals(found.Content.Block.Hash) && found.MessageType == Type.Propose);
        } else {
            ReceivedMessages.Find(found => mensaje.Content.Block.Hash.Equals(found.Content.Block.Hash) && found.MessageType == Type.Propose);
        }
        return found.Content.Block;
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

    public static List<Transaction> RandomTransactionListGenerator(){

        List<Transaction> transactions = new();
        Random r = new();
        for (int i = 0; i < r.NextInt64(6) + 1; i++){
            Transaction t = new(){
                Sender = r.Next(100),
                Receiver=r.Next(100),
                Id=r.Next(1000),
                Amount=r.Next(100000)
            };
            transactions.Add(t);
        }

        return transactions;
    }
    
    public static bool IsMessageReceived(Message message) {
        if (message.MessageType == Type.Echo) {
            return ReceivedMessages.Contains(message.Content.Message) || ReceivedMessages.Contains(message);
        }

        return ReceivedMessages.Contains(message);
    }

    public static bool ContentAlreadyReceived(Message content){
        foreach (Message item in ReceivedMessages) {
            if(item.MessageType==Type.Echo) {
                if(item.Content.Message.Equals(content)) return true;
            }
        }
        return false;
    }



    public static bool IsLeader(){
        return IDNode.Equals(Leader);
    }
    
}
