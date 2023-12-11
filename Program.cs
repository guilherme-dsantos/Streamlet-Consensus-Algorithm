using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Google.Protobuf;
using System.Linq;
internal class Program{

    // Configuration
    public static int TotalNodes = File.ReadLines("ips.txt").Count();
    public static int IDNode;
    public static List<TcpClient> OtherAddresses = new();
    public static List<Message> ReceivedMessages = new();
    public static volatile int Votes = 0;
    public static string[] SelfAddress = new string[2];
    public static NetworkStream? MyselfStream;
    public static string BlockchainFilePath = "";

    // Consensus and Voting
    public static bool VotedInEpoch; // Nodes can only vote at most once on each epoch
    public static int Epoch = 0;
    public static int DeltaEpoch = 3_000;

    public static Dictionary<ByteString, Block> ProposedBlocks = new();
    // Blockchain
    public static LinkedList<Block> BlockChain = new();

    // Thread Synchronization
    public static readonly object MessageLock = new();
    public volatile static CountdownEvent cde = new(TotalNodes);

    // Leader Election
    public static int Leader;

    //Fork
    public static int confusion_start = 0;
    public static int confusion_duration = 2;
    public static Queue<Message> queue = new();


    
    public static List<string> LostTransactions = new();

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

        BlockchainFilePath = "Blockchain_files/blockchain_from_node_" + IDNode + ".txt";
        
        if (File.Exists(BlockchainFilePath)){
            File.Delete(BlockchainFilePath);
        }
        
        // Reads the corresponding address from the ips.txt file
        string address_from_file = File.ReadLines("ips.txt").Skip(IDNode-1).Take(1).First();  

        //Genesis block
        BlockChain.AddLast(InitBlock());
    
        AppendLinesToFile(BlockchainFilePath, "Block Epoch: " + InitBlock().Epoch + " Lenght: " + InitBlock().Length + " Transaction: " + InitBlock().Transactions);

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
        Timer failureDetector = new(_ => CheckClientConnections(), null, 0, 100);
        while(true) {
            //Clears received messages at the start of each epoch for performance reasons
            ReceivedMessages.Clear();
            //Sets voted to false at the start of each epoch
            VotedInEpoch = false; 
            ++Epoch;
            if(random.NextDouble() < 0.4 && IDNode == 1) {
                Console.WriteLine("Sleeping...");
                Thread.Sleep(12000);
            }
                
            // Generate random number with given seed
            //Leader = random.Next(TotalNodes) + 1;
            Leader = GetLeader(random);
             Console.WriteLine("Epoch: "+ Epoch);
            Console.WriteLine("Leader: "  +Leader);
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
                    queue.Enqueue(receivedMessage);
                    if (queue.Count > 0) {
                        if (Epoch < confusion_start || Epoch >= confusion_start + confusion_duration - 1) {
                            receivedMessage = queue.Dequeue();
                        }
                    }
                    if (!IsMessageReceived(receivedMessage)) {
                        AddMessage(receivedMessage);
                        //Console.WriteLine("Message received: " + receivedMessage);
                        Thread.Sleep(100);
                        HandleEcho(receivedMessage);
                        if(IsProposeOrEchoPropose(receivedMessage)) {
                            //ProposedBlock = receivedMessage.MessageType == Type.Propose ? receivedMessage.Content.Block : receivedMessage.Content.Message.Content.Block;
                            if(receivedMessage.MessageType == Type.Propose){
                                if(!ProposedBlocks.ContainsKey(receivedMessage.Content.Block.Hash)) {
                                    ProposedBlocks.Add(receivedMessage.Content.Block.Hash,receivedMessage.Content.Block);   
                                }
                            }
                            if(receivedMessage.MessageType == Type.Echo){
                                if(!ProposedBlocks.ContainsKey(receivedMessage.Content.Message.Content.Block.Hash)) {
                                    ProposedBlocks.Add(receivedMessage.Content.Message.Content.Block.Hash, receivedMessage.Content.Message.Content.Block);
                                }
                            }

                            //Interlocked.Exchange(ref Votes,0);
                            if(receivedMessage.MessageType == Type.Echo && !VotedInEpoch /*&& IsBlockValid(receivedMessage.Content.Message.Content.Block)*/) {
                                BroadcastVotes(receivedMessage);
                                VotedInEpoch = true;
                            }
                        }
                        if(IsVoteOrEchoVote(receivedMessage)) {
                            UpdateNumVotes(receivedMessage);
                        
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
        Console.WriteLine(serializedMessage);
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
           try {
            NetworkStream otherStream = otherClient.GetStream();
            // Send only the relevant bytes received, not the entire buffer
            otherStream.Write(serializedMessage, 0, serializedMessage.Length);
            } catch (IOException) {
                //Console.Error.WriteLine("Error sending echo: " + ex.Message);
                OtherAddresses.Remove(otherClient);
                otherClient.Close();
            }
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
            Transactions = ""
        };
        return block;
    }
    /**
        * Function to propose a block
        */
    public static void ProposeBlock(int node_id, byte[] hash, int epoch, int length, string transactions){

        Block block = new(){
            Hash = ByteString.CopyFrom(hash),
            Epoch = epoch,
            Length = length,
            Transactions = transactions + string.Join(", ", LostTransactions)
        };

        Message messageWithBlock = new() {
            MessageType = Type.Propose,
            Content = new() {
                Block = block,
            },
            Sender = node_id
        };

        LostTransactions.Clear();
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
    public static Block GetLastFinalizedBlock(){
        if (BlockChain.Count >= 3 ) {
                // Get the last node
            LinkedListNode<Block>? lastNode = BlockChain.Last;
            List<Block> lastThreeBlocks = new();
            // Traverse backward and add values to the list
            for (int i = 0; i < 3 && lastNode != null; i++) {
                lastThreeBlocks.Insert(0, lastNode.Value); // Insert at the beginning of the list
                lastNode = lastNode.Previous;
            }
            if(lastThreeBlocks[0].Epoch == lastThreeBlocks[1].Epoch - 1 && lastThreeBlocks[1].Epoch == lastThreeBlocks[2].Epoch - 1 &&
            lastThreeBlocks[0].Length == lastThreeBlocks[1].Length - 1 && lastThreeBlocks[1].Length == lastThreeBlocks[2].Length - 1) {
                // Find the last 3rd node
                lastNode = BlockChain.Last;
                for (int i = 0; i < 2 && lastNode != null; i++) {
                    lastNode = lastNode.Previous;
                }

                List<Block> finalizedChain = new();

                // Traverse from the last 3rd node until the end
                while (lastNode != null){
                    // Access the value of the current node
                    Block b = lastNode.Value;
                    if(!BlockAlreadyFinalized("Block Epoch: " + b.Epoch.ToString())){
                       finalizedChain.Add(b);
                    } else {break;}
                    // Move to the previous node
                    lastNode = lastNode.Previous;
                }
                for (int i = finalizedChain.Count - 1; i >= 0; i--) { 
                    AppendLinesToFile(BlockchainFilePath, "Block Epoch: " + finalizedChain[i].Epoch + " Lenght: " + finalizedChain[i].Length + " Transaction: " + finalizedChain[i].Transactions);
                }
                finalizedChain.Clear();
                AppendLinesToFile(BlockchainFilePath, "Block Epoch: " + lastThreeBlocks[1].Epoch + " Lenght: " + lastThreeBlocks[1].Length + " Transaction: " + lastThreeBlocks[1].Transactions);
            return lastThreeBlocks[1];
            }
        }
        return InitBlock();
    }
    /**
        * Function to broadcast votes
        */
    public static void BroadcastVotes(Message messageWithBlock){

        Block originalBlock = messageWithBlock.Content.Message.Content.Block;
        Block voteBlock = new() {
            Hash = originalBlock.Hash,
            Epoch = originalBlock.Epoch,
            Length = originalBlock.Length,
            Transactions = "0",  // Change the transaction for the voting process
        };

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
    public static bool IsBlockValid(Block block) {
        lock(MessageLock) {
            //Block block = message.Content.Block;
            int maxId = BlockChain.Max(block => block.Epoch);
            if(block.Length <= GetLastFinalizedBlock().Length) {
                LostTransactions.Add(block.Transactions);
                return false;
            }
            if(block.Epoch <= maxId) {
                LostTransactions.Add(block.Transactions);
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
        } catch (InvalidProtocolBufferException) {
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
    
    static void AppendLinesToFile(string filePath, string line) {
        // Use FileMode.Create to create a new file or truncate an existing file
        using StreamWriter writer = new(filePath, true);
        writer.WriteLine(line);
    }

    static bool IsConnected(TcpClient client) {
    try {
        if (client != null && client.Client != null && client.Client.Connected) {
            return !(client.Client.Poll(1, SelectMode.SelectRead) && client.Client.Available == 0);
        } else
            return false;
    } catch {
        return false;
    }
   }
   public static void CheckClientConnections() {
        foreach (TcpClient client in OtherAddresses.ToList()) {
            if (!IsConnected(client)) {
                OtherAddresses.Remove(client);
            }
        }
    }

    static void UpdateNumVotes(Message receivedMessage) {
        lock(MessageLock) {
            ByteString hashToUpdate;

            if(receivedMessage.MessageType == Type.Vote) {
                hashToUpdate = receivedMessage.Content.Block.Hash; 
            }
            else {
                hashToUpdate = receivedMessage.Content.Message.Content.Block.Hash;
            }

        
            // Try to get the block with the specified hash
            if (ProposedBlocks.TryGetValue(hashToUpdate, out Block? blockToUpdate)) {
                // Update the NumVotes attribute
                
                blockToUpdate.NumVotes += 1;
                //Console.WriteLine("Voted for " + blockToUpdate);
                if(blockToUpdate.NumVotes > TotalNodes / 2 ) {
                    int length = blockToUpdate.Length;
                    LinkedListNode<Block>? blockNode = null;
                    
                    for (var node = BlockChain.First; node != null; node = node.Next) {
                        if (node.Value.Length == length - 1) {
                            blockNode = node;
                            break;
                        }
                    }

                    BlockChain.AddAfter(blockNode!, blockToUpdate);
                
                    //BlockChain.AddLast(blockToUpdate);
                    Console.WriteLine("Added block with length: " + blockToUpdate.Length);
                    //Console.WriteLine("Added to the blockchain" + blockToUpdate);
                    Block lastFinalizedBlock = GetLastFinalizedBlock();
                    ReceivedMessages.RemoveAll(message => message.Content.Block.Epoch <= lastFinalizedBlock.Epoch);
                    var block = BlockChain.First;
                    while (block != null) {
                        var next = block.Next;
                        if (block.Value.Epoch < lastFinalizedBlock.Epoch)
                            BlockChain.Remove(block);
                        block = next;
                    }
                    //AppendLinesToFile(BlockchainFilePath, "Block Epoch: " + InitBlock().Epoch + " Lenght: " + InitBlock().Length + " Transaction: " + InitBlock().Transactions);
                    ProposedBlocks.Remove(hashToUpdate);
                }
            }
            else {
                //Console.WriteLine("Dont need more votes, block already notarized");
            }
        }
    }

    static bool BlockAlreadyFinalized(string targetBlock)
    {
        // Check if the file exists
        if (!File.Exists(BlockchainFilePath))
        {
            Console.WriteLine($"File not found: {BlockchainFilePath}");
            return false;
        }

        // Read the file line by line
        using StreamReader reader = new(BlockchainFilePath);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Check if the target word exists in the current line
            if (line.Contains(targetBlock))
            {
                return true;
            }
        }

        // The word was not found in the file
        return false;
    }

    public static int GetLeader(Random random){
        if (Epoch < confusion_start || Epoch >= confusion_start + confusion_duration - 1) {
            return random.Next(1, TotalNodes + 1);
        }  
        else{
            return Epoch % TotalNodes;
        }
            
    }

}