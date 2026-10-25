/* Thread ABI: isolated heap snapshots in, portable graph packets out. No shared heap. */
/* The Thread library: ids, bilingual names and arities live only here; the host runs threads through service 4. */
#define THREAD_FIRST_ID 100u
#define THREAD_COUNT 8u
static const uint16_t *const thread_module_names[2]={u"Thread",u"執行緒"};
static const uint16_t *const thread_names[THREAD_COUNT][2]={{u"Start",u"啟動"},{u"Wait",u"等待"},{u"Result",u"結果"},{u"Cancel",u"取消"},{u"Close",u"關閉"},{u"IsDone",u"是否完成"},{u"Sleep",u"休眠"},{u"IsCancellationRequested",u"是否要求取消"}};
static const unsigned char thread_minimum[THREAD_COUNT]={1,1,1,1,1,1,1,0};
static const unsigned char thread_maximum[THREAD_COUNT]={2,2,1,1,2,1,1,0};
static Scope *thread_library(void) {
    Scope*s=scope(0);s->captured=1;for(u32 i=0;i<THREAD_COUNT;i++)for(u32 j=0;j<2;j++)declare(s,utf16(thread_names[i][j]),value(BUILTIN,THREAD_FIRST_ID+i,0));return s;
}
static int thread_arguments(Frame*f,u32 id,Value*a,u32 n) {
    if(id<THREAD_FIRST_ID||id>=THREAD_FIRST_ID+THREAD_COUNT||n<thread_minimum[id-THREAD_FIRST_ID]||n>thread_maximum[id-THREAD_FIRST_ID]){failure(f,"Incorrect Thread argument count.");return 0;}
    if(id==100){
        if(a[0].tag!=CLOSURE||(n==2&&a[1].tag!=ARRAY)){failure(f,"Thread.Start expects a function and optional argument array.");return 0;}
        Closure*c=pointer(a[0].ref);u32 count=n==2?((Array*)pointer(a[1].ref))->count:0;
        if(c->program->functions[c->function].arity!=count){failure(f,"Thread.Start function argument count mismatch.");return 0;}
    }
    if((id==106)||(n==2&&(id==101||id==104))){Value t=a[id==106?0:1];
        if(t.tag!=INT||t.number<(id==106?0:-1)||t.number>2147483647){failure(f,"Thread timeout must be an integer in milliseconds (-1 means infinite wait).");return 0;}}
    return 1;
}
u32 aqua_thread_module(void) { return address(thread_library()); }

#define THREAD_PACKET_LIMIT (16u*1024*1024)
#define THREAD_GRAPH_LIMIT 4096u
typedef struct { unsigned char*data;u32 size,capacity,position,failed,count;u32 refs[THREAD_GRAPH_LIMIT];Value values[THREAD_GRAPH_LIMIT]; } ThreadPacket;
static void packet_write(ThreadPacket*p,const void*data,u32 n) {
    if(p->failed||n>THREAD_PACKET_LIMIT-p->size){p->failed=1;return;}
    if(p->size+n>p->capacity){u32 cap=p->capacity?p->capacity:256;while(cap<p->size+n)cap*=2;unsigned char*next=allocate(cap,RAW);if(p->size)memcpy(next,p->data,p->size);release(p->data);p->data=next;p->capacity=cap;}
    memcpy(p->data+p->size,data,n);p->size+=n;
}
static void packet_read(ThreadPacket*p,void*out,u32 n) { if(p->failed||n>p->size-p->position){p->failed=1;memset(out,0,n);return;}memcpy(out,p->data+p->position,n);p->position+=n; }
static void packet_encode(ThreadPacket*p,Value v,u32 depth) {
    if(p->failed)return;if(depth>128){p->failed=1;return;}
    if(v.tag==ARRAY||v.tag==HASH){for(u32 i=0;i<p->count;i++)if(p->refs[i]==v.ref){u32 tag=15;packet_write(p,&tag,4);packet_write(p,&i,4);return;}
        if(p->count==THREAD_GRAPH_LIMIT){p->failed=1;return;}p->refs[p->count++]=v.ref;}
    packet_write(p,&v.tag,4);
    if(v.tag>=INT&&v.tag<=BOOL)packet_write(p,&v.number,8);
    else if(v.tag==STRING||v.tag==ERROR){Text*t=pointer(v.ref);packet_write(p,&t->length,4);packet_write(p,t->data,t->length*2);}
    else if(v.tag==ARRAY){Array*a=pointer(v.ref);packet_write(p,&a->count,4);for(u32 i=0;i<a->count&&!p->failed;i++)packet_encode(p,a->values[i],depth+1);}
    else if(v.tag==HASH){Hash*h=pointer(v.ref);packet_write(p,&h->count,4);for(u32 i=0;i<h->count&&!p->failed;i++){packet_encode(p,h->pairs[i].key,depth+1);packet_encode(p,h->pairs[i].value,depth+1);}}
    else if(v.tag!=VOID&&v.tag!=NULL_VALUE)p->failed=1;
}
static Value packet_decode(ThreadPacket*p,u32 depth) {
    if(p->failed||depth>128){p->failed=1;return empty;}u32 tag,n;packet_read(p,&tag,4);Value v=value(tag,0,0);
    if(tag==15){packet_read(p,&n,4);if(n>=p->count){p->failed=1;return empty;}return p->values[n];}
    if(tag>=INT&&tag<=BOOL)packet_read(p,&v.number,8);
    else if(tag==STRING||tag==ERROR){packet_read(p,&n,4);if(n>(p->size-p->position)/2){p->failed=1;return empty;}Text*t=pointer(aqua_text(n));packet_read(p,t->data,n*2);v.ref=address(t);}
    else if(tag==ARRAY||tag==HASH){packet_read(p,&n,4);if(n>p->size/4||p->count==THREAD_GRAPH_LIMIT){p->failed=1;return empty;}
        v.ref=tag==ARRAY?aqua_array(n):aqua_hash(n);p->values[p->count++]=v;
        for(u32 i=0;i<n&&!p->failed;i++){if(tag==ARRAY)((Array*)pointer(v.ref))->values[i]=packet_decode(p,depth+1);
            else{Hash*h=pointer(v.ref);h->pairs[i].key=packet_decode(p,depth+1);h->pairs[i].value=packet_decode(p,depth+1);}}
    }else if(tag!=VOID&&tag!=NULL_VALUE)p->failed=1;return v;
}
u32 aqua_thread_encode(u32 cell) {
    ThreadPacket p={0};u32 zero=0;packet_write(&p,&zero,4);packet_encode(&p,*(Value*)pointer(cell),0);
    if(p.failed){release(p.data);return 0;}*(u32*)p.data=p.size-4;return address(p.data);
}
u32 aqua_thread_decode(u32 packet) {
    ThreadPacket p={0};p.size=*(u32*)pointer(packet);if(p.size>THREAD_PACKET_LIMIT)return 0;p.data=(unsigned char*)pointer(packet)+4;
    Value v=packet_decode(&p,0);if(p.failed||p.position!=p.size)return 0;Value*out=allocate(sizeof(Value),RAW);*out=v;return address(out);
}
u32 aqua_thread_free_packet(u32 packet) { release(pointer(packet));return 0; }
