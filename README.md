# Implementation of the Streamlet Consensus Algorithm in Blockchain

Class project for the Distributed Fault Tolerance Course.
FCUL 2023

## Table of Contents

1. [Getting Started](#getting-started)
   - [Prerequisites](#prerequisites)
   - [Installation](#installation)
2. [Usage](#usage)
3. [References](#references)

## Getting Started

Follow these steps to set up and run the project on your local machine.

### Prerequisites

Before you begin, make sure you have the following installed:

1. **.NET 7 SDK:** This project requires .NET 7 SDK for building and running. Download and install it from the official [.NET website](https://dotnet.microsoft.com/download/dotnet/7.0).

### Installation

1. **Clone the Repository:**

   ```bash
   git clone https://github.com/guilherme-dsantos/DFT.git
   cd DFT

   ```

2. **Build the Project:**
   ```bash
   dotnet build
   ```

## Usage

1. **Run the project:**

   ```bash
   dotnet run <node_id>
   ```

   Replace `<node_id>` with the specific node ID you want to assign to the running instance. Each node in your system should have a unique ID between \(1\) and \(5\), and users should provide this ID as a command-line argument when starting the application. The number of the ID corresponds to the line of the file `ips.txt`, which addresses the node with the given IP address on that line.

2. **Explore the Streamlet Blockchain:**

   Once the project is running, explore the Streamlet blockchain and observe the consensus algorithm in action.

## References

Michel Raynal. Fault-Tolerant Message-Passing Distributed Systems: An Algorithmic Approach. Springer. 2018. [Ebook] https://link.springer.com/book/10.1007/978-3-319-94141-7

Streamlet: Textbook Streamlined Blockchains: [Paper] https://dl.acm.org/doi/10.1145/3419614.3423256
