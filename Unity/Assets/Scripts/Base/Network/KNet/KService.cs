﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Model
{
	public static class KcpProtocalType
	{
		public const uint SYN = 1;
		public const uint ACK = 2;
	}

	public sealed class KService: AService
	{
		private uint IdGenerater = 1000;

		public uint TimeNow { get; set; }

		private UdpClient socket;
		
		private readonly Dictionary<long, KChannel> idChannels = new Dictionary<long, KChannel>();

		private readonly Queue<UdpReceiveResult> udpResults = new Queue<UdpReceiveResult>();

		private TaskCompletionSource<AChannel> acceptTcs;

		private readonly Queue<long> removedChannels = new Queue<long>();

		// 下帧要更新的channel
		private readonly HashSet<long> updateChannels = new HashSet<long>();

		// 下次时间更新的channel
		private readonly MultiMap<long, long> timerMap = new MultiMap<long, long>();
		private readonly Queue<long> timeoutTimer = new Queue<long>();

		public KService(string host, int port)
		{
			this.TimeNow = (uint)TimeHelper.Now();
			this.socket = new UdpClient(new IPEndPoint(IPAddress.Parse(host), port));
			this.StartRecv();
		}

		public KService()
		{
			this.TimeNow = (uint)TimeHelper.Now();
			this.socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
			this.StartRecv();
		}

		public override void Dispose()
		{
			if (this.socket == null)
			{
				return;
			}

			this.socket.Dispose();
			this.socket = null;
		}

		public async void StartRecv()
		{
			while (true)
			{
				if (this.socket == null)
				{
					return;
				}
				UdpReceiveResult udpReceiveResult = await this.socket.ReceiveAsync();

				// accept
				uint conn = BitConverter.ToUInt32(udpReceiveResult.Buffer, 0);
				
				// conn从1000开始，如果为1，2则是特殊包
				if (conn == KcpProtocalType.SYN)
				{
					this.HandleAccept(udpReceiveResult);
					continue;
				}
				
				// connect response
				if (conn == KcpProtocalType.ACK)
				{
					this.HandleConnect(udpReceiveResult);
					continue;
				}

				if (conn < 1000)
				{
					Log.Error($"error conn: {conn} {udpReceiveResult.RemoteEndPoint}");
					continue;
				}

				this.HandleRecv(udpReceiveResult);
			}
		}

		private void HandleConnect(UdpReceiveResult udpReceiveResult)
		{
			uint requestConn = BitConverter.ToUInt32(udpReceiveResult.Buffer, 4);
			uint responseConn = BitConverter.ToUInt32(udpReceiveResult.Buffer, 8);
			KChannel kChannel;
			if (!this.idChannels.TryGetValue(requestConn, out kChannel))
			{
				return;
			}
			// 处理chanel
			kChannel.HandleConnnect(responseConn);
		}

		private void HandleRecv(UdpReceiveResult udpReceiveResult)
		{
			uint conn = 0;
			Kcp.ikcp_decode32u(udpReceiveResult.Buffer, 0, ref conn);

			KChannel kChannel;
			if (!this.idChannels.TryGetValue(conn, out kChannel))
			{
				return;
			}
			// 处理chanel
			kChannel.HandleRecv(udpReceiveResult.Buffer, this.TimeNow);
		}

		private void HandleAccept(UdpReceiveResult udpReceiveResult)
		{
			if (this.acceptTcs == null)
			{
				this.udpResults.Enqueue(udpReceiveResult);
				return;
			}

			uint requestConn = BitConverter.ToUInt32(udpReceiveResult.Buffer, 4);
			
			// 如果已经连接上,则重新响应请求
			KChannel kChannel;
			if (this.idChannels.TryGetValue(requestConn, out kChannel))
			{
				kChannel.HandleAccept(requestConn);
				return;
			}
			
			TaskCompletionSource<AChannel> t = this.acceptTcs;
			this.acceptTcs = null;
			kChannel = this.CreateAcceptChannel(udpReceiveResult.RemoteEndPoint, requestConn);
			kChannel.HandleAccept(requestConn);
			t.SetResult(kChannel);
		}

		private KChannel CreateAcceptChannel(IPEndPoint remoteEndPoint, uint remoteConn)
		{
			KChannel channel = new KChannel(++this.IdGenerater, remoteConn, this.socket, remoteEndPoint, this);
			this.idChannels[channel.Id] = channel;
			return channel;
		}
		
		private KChannel CreateConnectChannel(IPEndPoint remoteEndPoint)
		{
			KChannel channel = new KChannel(++this.IdGenerater, this.socket, remoteEndPoint, this);
			this.idChannels[channel.Id] = channel;
			return channel;
		}

		public void AddToUpdate(long id)
		{
			this.updateChannels.Add(id);
		}

		public void AddToNextTimeUpdate(long time, long id)
		{
			this.timerMap.Add(time, id);
		}

		public override void Add(Action action)
		{
		}

		public override AChannel GetChannel(long id)
		{
			KChannel channel;
			this.idChannels.TryGetValue(id, out channel);
			return channel;
		}

		public override Task<AChannel> AcceptChannel()
		{
			if (this.udpResults.Count > 0)
			{
				UdpReceiveResult udpReceiveResult = this.udpResults.Dequeue();
				uint requestConn = BitConverter.ToUInt32(udpReceiveResult.Buffer, 4);
				KChannel kChannel = this.CreateAcceptChannel(udpReceiveResult.RemoteEndPoint, requestConn);
				return Task.FromResult<AChannel>(kChannel);
			}

			acceptTcs = new TaskCompletionSource<AChannel>();
			return this.acceptTcs.Task;
		}

		public override AChannel ConnectChannel(string host, int port)
		{
			IPEndPoint ipEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
			KChannel channel = this.CreateConnectChannel(ipEndPoint);
			return channel;
		}


		public override void Remove(long id)
		{
			KChannel channel;
			if (!this.idChannels.TryGetValue(id, out channel))
			{
				return;
			}
			if (channel == null)
			{
				return;
			}
			this.removedChannels.Enqueue(id);
			channel.Dispose();
		}
		
		public override void Update()
		{
			this.TimeNow = (uint)TimeHelper.Now();
			
			foreach (long time in this.timerMap.Keys)
			{
				if (time > this.TimeNow)
				{
					break;
				}
				this.timeoutTimer.Enqueue(time);
			}
			while (this.timeoutTimer.Count > 0)
			{
				long key = this.timeoutTimer.Dequeue();
				List<long> timeOutId = this.timerMap[key];
				foreach (long id in timeOutId)
				{
					this.updateChannels.Add(id);
				}
				this.timerMap.Remove(key);
			}

			foreach (long id in updateChannels)
			{
				KChannel kChannel;
				if (!this.idChannels.TryGetValue(id, out kChannel))
				{
					continue;
				}
				if (kChannel.Id == 0)
				{
					continue;
				}
				kChannel.Update(this.TimeNow);
			}
			this.updateChannels.Clear();

			while (true)
			{
				if (this.removedChannels.Count <= 0)
				{
					break;
				}
				long id = this.removedChannels.Dequeue();
				this.idChannels.Remove(id);
			}
		}
	}
}