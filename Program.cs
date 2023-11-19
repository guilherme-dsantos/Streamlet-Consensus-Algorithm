
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;

internal class Program{

    // Network Configuration
    public static int TotalNodes = File.ReadLines("ips.txt").Count();
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
        
        // Reads the corresponding address from the ips.txt file
        string address_from_file = File.ReadLines("ips.txt").Skip(IDNode-1).Take(1).First();  

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
        }

        // Connect to myself so I can URB_Broadcast to myself
        TcpClient myself = new();
        myself.Connect(IPAddress.Parse(SelfAddress[0]), int.Parse(SelfAddress[1]));
        MyselfStream = myself.GetStream();

        // Wait for all nodes to be ready
        cde.Wait();
        while(true) {
            //Clears received messages at the start of each epoch for performance reasons
            ReceivedMessages.Clear();
            //Sets voted to false at the start of each epoch
            VotedInEpoch = false; 
            ++Epoch;
            // Generate random number with given seed
            Leader = random.Next(TotalNodes) + 1;

            // If I am the leader, propose a block
            if(IsLeader()) { 
                byte[] hash = ComputeParentHash();
                ProposeBlock(IDNode, hash, Epoch, BlockChain.Last().Length + 1,GenerateTransactions());
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
                        AddMessage(receivedMessage);
                        Thread.Sleep(100);
                        HandleEcho(receivedMessage);
                        if(IsProposeOrEchoPropose(receivedMessage)) {
                            ProposedBlock = receivedMessage.MessageType == Type.Propose ? receivedMessage.Content.Block : receivedMessage.Content.Message.Content.Block;
                            Interlocked.Exchange(ref Votes,0);
                            if(receivedMessage.MessageType == Type.Echo && !VotedInEpoch && IsBlockValid(receivedMessage.Content.Message)) {
                                BroadcastVotes(receivedMessage);
                                VotedInEpoch = true;
                            }
                        }
                        if(IsVoteOrEchoVote(receivedMessage)) {
                            Interlocked.Increment(ref Votes);
                            if(Votes > TotalNodes / 2) {
                                BlockChain.Add(ProposedBlock);
                                Interlocked.Exchange(ref Votes,0);
                                CheckFinalizationCriteria();
                                ProposedBlock = new();
                                Console.WriteLine("====={Blockchain}=====");
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

    /**
        * Function to broadcast a message
        */
    public static void URB_Broadcast(Message message) {
        // Serialize the message to bytes
        byte[] serializedMessage = SerializeMessage(message);
        // Send the serialized message to myself
        MyselfStream?.Write(serializedMessage, 0, serializedMessage.Length);
    }
    /**
        * Function to echo a message
        */
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
    /**
        * Function to initialize the genesis block
        */
    public static Block InitBlock() {
        Block block = new() {
            Hash = ByteString.CopyFrom(new byte[] {0}),
            Epoch = 0,
            Length = 0,
            Transactions = {}
        };
        return block;
    }
    /**
        * Function to propose a block
        */
    public static void ProposeBlock(int node_id, byte[] hash, int epoch, int length, string transactions){
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
                Transactions = transactions  // Initialize as a new list
            }
        },
        Sender = node_id
    };

        URB_Broadcast(messageWithBlock);
    }
    /**
        * Function to add a message to the list of received messages
        */
    public static void AddMessage(Message receivedMessage) {
        lock(MessageLock) {
            if (receivedMessage.MessageType == Type.Echo) {
            ReceivedMessages.Add(receivedMessage.Content.Message);
            } else {
             ReceivedMessages.Add(receivedMessage);
            }
        }
    }
    /**
        * Function to handle the echo message
        */
    public static void HandleEcho(Message receivedMessage){
        lock(MessageLock) {
            if(receivedMessage.MessageType == Type.Echo) {
                Echo(receivedMessage.Content.Message);
            } else {
                Echo(receivedMessage);
            }
        }
    }
    /**
        * Function to check if the message is a propose or an echo propose
        */
    public static bool IsProposeOrEchoPropose(Message receivedMessage) {
        lock(MessageLock){
            if(receivedMessage.MessageType == Type.Propose || (receivedMessage.MessageType == Type.Echo && receivedMessage.Content.Message.MessageType == Type.Propose)) {
                return true;
            }
                return false;
        }
        
    }
    /**
        * Function to check if the message is a vote or an echo vote
        */
    public static bool IsVoteOrEchoVote(Message receivedMessage) {
        lock(MessageLock) {
            if(receivedMessage.MessageType == Type.Vote || (receivedMessage.MessageType == Type.Echo && receivedMessage.Content.Message.MessageType == Type.Vote)) {
                return true;
            }
                return false;
        }
        
    }
    /**
        * Function to check if the finalization criteria is met
        */
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
    /**
        * Function to broadcast votes
        */
    public static void BroadcastVotes(Message messageWithBlock){
        Block voteBlock = messageWithBlock.Content.Message.Content.Block;
        voteBlock.Transactions = "0";
        Message voteMessage = new() {
            MessageType = Type.Vote,
            Content = new Content { 
                Block = voteBlock
            },
        Sender = IDNode
        };
        URB_Broadcast(voteMessage);
    }
    /**
        * Function to check if a block is valid
        */
    public static bool IsBlockValid(Message message) {
        lock(MessageLock) {
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
        
    }
    /**
        * Function to compute the hash of the last block in the blockchain
        */
    public static byte[] ComputeParentHash() {
        Block lastBlock = BlockChain.Last();
        byte[] hash = lastBlock.Hash.ToByteArray();
        byte[] newHash = SHA1.HashData(hash);
        return newHash;
    }

    /**
        * Function to serialize a Message object into a byte array
        */
    public static byte[] SerializeMessage(Message message) {
        using MemoryStream stream = new();
        message.WriteTo(stream);
        return stream.ToArray();
    }

   /**
        * Function to deserialize a byte array into a Message object
        * Returns null if the deserialization fails
        */
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
    /**
        * Returns true if the message has already been received
        */  
    public static bool IsMessageReceived(Message message) {
        lock(MessageLock) {
            if (message.MessageType == Type.Echo) {
            return ReceivedMessages.Contains(message.Content.Message) || ReceivedMessages.Contains(message);
        }
        return ReceivedMessages.Contains(message);
        } 
    }
    /**
        * Returns true if the node is the leader of the current epoch
        */
    public static bool IsLeader(){
        return IDNode.Equals(Leader);
    }

     /** Generate a random transaction in the format:
        * TransactionType | AccountType | Currency | Amount
        * Example: Purchase | Checking | USD | 123.45
    */

    public static string GenerateTransactions() {
        int numberOfTransactions = new Random().Next(2, 5); 
        string result = "";

        for (int i = 0; i < numberOfTransactions; i++) {
            string transaction = GenerateTransaction();
            result += transaction + " || ";
        }

        return result.Trim();
    }

   
    public static string GenerateTransaction() {
        string transactionType = GetRandomTransactionType();
        string accountType = GetRandomAccountType();
        string currency = GetRandomCurrency();
        decimal amount = GetRandomAmount();

        return $"{transactionType} | {accountType} | {currency} | {amount:F2}";
    }

    public static string GetRandomTransactionType(){
        string[] transactionTypes = { "Purchase", "Deposit", "Withdrawal", "Transfer" };
        return transactionTypes[new Random().Next(transactionTypes.Length)];
    }

    public static string GetRandomAccountType(){
        string[] accountTypes = { "Savings", "Checking", "CreditCard" };
        return accountTypes[new Random().Next(accountTypes.Length)];
    }

    public static string GetRandomCurrency(){
        string[] currencies = { "USD", "EUR", "GBP" };
        return currencies[new Random().Next(currencies.Length)];
    }

    public static decimal GetRandomAmount(){
        return (decimal)new Random().Next(1, 10000) + (decimal)new Random().NextDouble();
    }
    
}
