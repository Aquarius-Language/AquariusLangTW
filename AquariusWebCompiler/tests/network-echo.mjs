// Test fixture for frontend/協定驗證.aqua. The example's web backend is Aquarius.
import net from 'node:net';
import dgram from 'node:dgram';
const tcp=net.createServer(socket=>{socket.on('error',()=>{});socket.on('data',bytes=>socket.write(bytes));});
const udp=dgram.createSocket('udp4');udp.on('message',(bytes,peer)=>udp.send(bytes,peer.port,peer.address));
await Promise.all([new Promise(resolve=>tcp.listen(8871,'127.0.0.1',resolve)),new Promise(resolve=>udp.bind(8872,'127.0.0.1',resolve))]);
console.log('TCP 8871 / UDP 8872 echo fixture ready');
for(const signal of ['SIGINT','SIGTERM'])process.on(signal,()=>{tcp.close();udp.close();});
