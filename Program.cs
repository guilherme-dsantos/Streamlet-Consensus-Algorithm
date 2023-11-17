﻿using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;

internal class Program{

    public const int TotalNodes = 5;

    public static int IDNode;
    public static List<TcpClient> OtherAddresses = new();
    public static List<Message> ReceivedMessages = new();
    public static volatile int Votes = 0;
    public static string[] SelfAddress = new string[2];

    public static int Epoch = 0;
    public static int DeltaEpoch = 5_000;
    public static Block proposedBlock = new();
    
    public static bool VotedInEpoch; //Nodes can only vote at most once on each epoch
    public static List<Block> BlockChain = new();
    
    public static readonly object MessageLock = new();
    public static readonly object WriteLock = new();
    public volatile static CountdownEvent cde = new(TotalNodes - 1);
    public static int Leader;
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
            string[] addresses = others.Split(":");
            TcpClient client = new();
            client.Connect(IPAddress.Parse(addresses[0]), int.Parse(addresses[1]));
            OtherAddresses.Add(client);
            //Console.WriteLine($"Connected to the target node at {addresses[0]}:{addresses[1]}");
        }

        // Wait for all nodes to be ready
        cde.Wait();
        while(true) {
            VotedInEpoch = false; //Sets voted to false at the start of each epoch
            
            // Generate random number with given seed
            Leader = random.Next(TotalNodes) + 1;

            // Print epoch number and leader to console in blue every time a new epoch starts
            Console.ForegroundColor = ConsoleColor.Blue;
            lock(WriteLock) {
                Console.WriteLine($"Epoch: {++Epoch} started with Leader {Leader}");
                Console.ResetColor();
            }
            //Console.WriteLine($"Leader is: {randomNumber}");
            if(IsLeader()) { //if leader, propose block
                byte[] hash = ComputeParentHash();
                ProposeBlock(IDNode, hash, Epoch, BlockChain.Last().Length + 1, new List<Transaction>());
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
                        if(receivedMessage.MessageType == Type.Propose) {
                            proposedBlock = receivedMessage.Content.Block;
                            Interlocked.Exchange(ref Votes,0);
                        }
                        //lock(WriteLock) {
                            //Console.WriteLine("Message Received: " + receivedMessage);
                        //}
                        ReceivedMessages.Add(receivedMessage);
                        // Echo message to the other nodes
                        Echo(receivedMessage);
                        if(receivedMessage.Sender != IDNode && receivedMessage.MessageType == Type.Propose && !VotedInEpoch && IsBlockValid(receivedMessage)) { //other nodes vote for proposed block
                            BroadcastVotes(receivedMessage);
                            VotedInEpoch = true;
                        }
                        if(receivedMessage.MessageType == Type.Vote && receivedMessage.Content.Block.Hash.Equals(proposedBlock.Hash)) {
                            Interlocked.Increment(ref Votes);
                            if(Votes > TotalNodes / 2) { //if all nodes voted, add block to blockchain
                                BlockChain.Add(proposedBlock);
                                CheckFinalizationCriteria();
                                lock(WriteLock){
                                    foreach(Block b in BlockChain) {
                                        Console.WriteLine(b);
                                    }
                                }
                                
                                proposedBlock = new();
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
        //Console.WriteLine("Broadcast Message: " + message);
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
            Transactions = {}
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
                    Transactions = {transactions},
                }
            },
            Sender = node_id
        };
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
            lock(WriteLock){
                Console.WriteLine("Finalized Epoch: " + lastBlocks[1].Epoch);
                Console.WriteLine("Pointer to last block finalized " + PointerLastFinalized);
            }
        }
    }

    public static void BroadcastVotes(Message proposedBlock){
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

    public static bool IsBlockValid(Message message) {
        Block block = message.Content.Block;
        byte[] hash = block.Hash.ToByteArray();
        byte[] newHash = ComputeParentHash();
        if(block.Length <= PointerLastFinalized) {
            Console.WriteLine("Block length is not valid");
            return false;
        }
        if (!hash.SequenceEqual(newHash)) {
            return false;
        }
        return true;
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
    
}
