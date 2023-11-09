
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal class Program{
    static void Main(string[] args){
        int node_id = int.Parse(args[0]);
        Console.WriteLine(node_id);
        string line = File.ReadLines("ips.txt").Skip(node_id).Take(1).First();
        Console.WriteLine(line);
        
        List<string> ips = new List<string>();      
        for(int i = 0; i < 3; i++){
            if(i.Equals(node_id)){
                continue;
            }
            
            string others = File.ReadLines("ips.txt").Skip(i).Take(1).First();
            Console.WriteLine("?????" + others);
            ips.Add(others);
        }
        Console.WriteLine("OTHERS:");
        ips.ForEach(Console.WriteLine);
        int seed = 42;

        String[] selfAddress = line.Split(":");
        
        
        // Start a thread for the listener
        Thread listenerThread = new Thread(() => StartListener(selfAddress[0], int.Parse(selfAddress[1])));
        listenerThread.Start();

        // Connect to multiple target nodes asynchronously
        ips.ForEach( str => {
            String[] adresses = str.Split(":");
            Task.Run(() => StartClient(adresses[0], int.Parse(adresses[1])));
        });
        
        
    
        // Create a Random object with the seed
        Random random = new Random(seed);

        // Generate random numbers
        //for (int i = 0; i < 5; i++){
            int randomNumber = random.Next(4);
            Console.WriteLine($"Leader is: {randomNumber}");
        //}
        if(node_id.Equals(randomNumber)){
            Console.WriteLine("Im the leader");
        }
        
        for(int i = 0; i < args.Length; i++){
            Console.WriteLine(args[i]);
        }
    }

    static void StartListener(string ipAddress, int port)
    {
        TcpListener listener = new TcpListener(IPAddress.Parse(ipAddress), port);
        listener.Start();
        Console.WriteLine("Listener " + port + " node is waiting for incoming connections...");

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            // Handle the incoming connection in a new thread or task
            Task.Factory.StartNew(() => HandleConnection(client));
        }
    }

    static void StartClient(string serverIpAddress, int serverPort)
    {
        Thread.Sleep(15000);
        TcpClient client = new TcpClient();
        client.Connect(IPAddress.Parse(serverIpAddress), serverPort);
        Console.WriteLine($"Connected to the target node at {serverIpAddress}:{serverPort}");

        // You can send and receive data with the target node here
    }

    static void HandleConnection(TcpClient client)
    {
        // Handle the incoming connection here
    }
}
