/* Builtin IDs 20..29: portable hash library. Included after arrays.h; shares its helpers. */
/* The Hash library: ids, bilingual names and arities live only here; the host never implements or lists them. */
#define HASH_FIRST_ID 20u
#define HASH_COUNT 10u
static const uint16_t *const hash_module_names[2]={u"Hash",u"雜湊"};
static const uint16_t *const hash_names[HASH_COUNT][2]={{u"Keys",u"鍵列表"},{u"Values",u"值列表"},{u"Has",u"包含鍵"},{u"Get",u"取得"},{u"Delete",u"刪除鍵"},{u"Len",u"長度"},{u"Clone",u"克隆"},{u"Clear",u"清空"},{u"Merge",u"合併"},{u"Entries",u"項目列表"}};
static const unsigned char hash_minimum[HASH_COUNT]={1,1,2,3,2,1,1,1,2,1};
static const unsigned char hash_maximum[HASH_COUNT]={1,1,2,3,2,1,1,1,2,1};
static Scope *hash_library(void) {
    Scope*s=scope(0);s->captured=1;for(u32 i=0;i<HASH_COUNT;i++)for(u32 j=0;j<2;j++)declare(s,utf16(hash_names[i][j]),value(BUILTIN,HASH_FIRST_ID+i,0));return s;
}
static void hash_changed(Hash*h) {
    if(h->exposed&&!h->dirty){h->dirty=1;h->dirty_next=pending.dirty_hashes;pending.dirty_hashes=h;}
}
static int hash_key(Frame*f,Value k) {
    if(numeric(k)||k.tag==STRING||k.tag==BOOL)return 1;
    named_failure(f,"Unusable as hash key: ",ascii(type_name(k.tag)));return 0;
}
/* Hash index: above HASH_INDEX_MIN pairs, an open-addressing (linear probing) table of (position + 1, key hash) slots
   over pairs[0..indexed), kept at most 3/4 full. index[0] is the slot count (a power of two); slots follow at index + 2.
   Lookups first index the pairs appended since (hash_sync), so a host that appends or truncates pairs directly keeps
   working: a truncation below `indexed` rebuilds the index. */
#define HASH_INDEX_MIN 8u
static u32 mix32(u32 h) { h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;h^=h>>16;return h; }
/* Consistent with equal(a, b, 1): numbers hash by their type and value (0 and -0 alike, every NaN alike). */
static u32 key_hash(Value k) {
    if(k.tag==STRING)return text_hash(pointer(k.ref));
    if(numeric(k)||k.tag==BOOL){double n=k.number;uint64_t bits=0;if(n!=n)bits=0x7ff8000000000000ull;else if(n!=0)memcpy(&bits,&n,8);return mix32((u32)bits^mix32((u32)(bits>>32)^k.tag));}
    return mix32(k.ref^k.tag*0x9e3779b9u);
}
static void index_put(u32*x,u32 position,u32 hash) { u32 mask=x[0]-1,s=hash&mask;while(x[2+s*2])s=(s+1)&mask;x[2+s*2]=position+1;x[3+s*2]=hash; }
static void hash_drop_index(Hash*h) { release(h->index);h->index=0;h->indexed=0; }
static void hash_sync(Hash*h) {
    if(h->indexed>h->count)hash_drop_index(h);
    if(h->index?h->indexed==h->count:h->count<=HASH_INDEX_MIN)return;
    if(!h->index||h->count*4>h->index[0]*3){
        u32 slots=16;while(slots<h->count*2)slots*=2;
        hash_drop_index(h);h->index=allocate((2+slots*2)*4,RAW);h->index[0]=slots;
    }
    for(;h->indexed<h->count;h->indexed++)index_put(h->index,h->indexed,key_hash(h->pairs[h->indexed].key));
}
/* The position of key k in h's pairs, or h->count when absent. */
static u32 hash_find(Hash*h,Value k) {
    hash_sync(h);
    if(!h->index){u32 j=0;while(j<h->count&&!equal(h->pairs[j].key,k,1))j++;return j;}
    u32 hash=key_hash(k),mask=h->index[0]-1,*x=h->index+2;
    for(u32 s=hash&mask;x[s*2];s=(s+1)&mask)if(x[s*2+1]==hash&&equal(h->pairs[x[s*2]-1].key,k,1))return x[s*2]-1;
    return h->count;
}
/* Removes pair j (found by hash_find) keeping the pairs dense and in order: its slot is deleted by backward shifting and
   the positions after it move down by one. */
static void hash_remove(Hash*h,u32 j) {
    if(h->index){
        u32 mask=h->index[0]-1,*x=h->index+2,s=key_hash(h->pairs[j].key)&mask;
        while(x[s*2]!=j+1)s=(s+1)&mask;
        u32 hole=s;
        for(u32 t=(s+1)&mask;x[t*2];t=(t+1)&mask){u32 home=x[t*2+1]&mask;if(((t-home)&mask)>=((t-hole)&mask)){x[hole*2]=x[t*2];x[hole*2+1]=x[t*2+1];hole=t;}}
        x[hole*2]=x[hole*2+1]=0;
        if(j+1<h->count)for(u32 t=0;t<=mask;t++)if(x[t*2]>j+1)x[t*2]--;
        h->indexed--;
    }
    memmove(h->pairs+j,h->pairs+j+1,(h->count-j-1)*sizeof(Pair));
    h->count--;memset(h->pairs+h->count,0,sizeof(Pair));
}
static Value hash_copy(Hash*source) {
    Hash*h=pointer(aqua_hash(source->count));memcpy(h->pairs,source->pairs,source->count*sizeof(Pair));return value(HASH,address(h),0);
}
static Value hash_call(Frame*f,u32 id,Value*args,u32 n) {
    if(id<HASH_FIRST_ID||id>=HASH_FIRST_ID+HASH_COUNT||n<hash_minimum[id-HASH_FIRST_ID]||n>hash_maximum[id-HASH_FIRST_ID]){failure(f,"Incorrect Hash argument count.");return empty;}
    if(args[0].tag!=HASH){failure(f,"Hash function requires a hash.");return empty;}
    Hash*h=pointer(args[0].ref);
    if(id==20||id==21||id==29){
        Array*a=array(h->count);
        for(u32 i=0;i<h->count;i++){
            if(id==20)array_put(a,i,h->pairs[i].key);
            else if(id==21)array_put(a,i,h->pairs[i].value);
            else{Array*p=array(2);array_put(p,0,h->pairs[i].key);array_put(p,1,h->pairs[i].value);array_put(a,i,value(ARRAY,address(p),0));}
        }
        return value(ARRAY,address(a),0);
    }
    if(id==22||id==23||id==24){
        if(!hash_key(f,args[1]))return empty;
        u32 j=hash_find(h,args[1]);
        if(id==22)return value(BOOL,0,j<h->count);
        if(id==23)return j<h->count?h->pairs[j].value:args[2];
        if(j==h->count)return value(BOOL,0,0);
        hash_remove(h,j);hash_changed(h);return value(BOOL,0,1);
    }
    if(id==25)return number(h->count,INT);
    if(id==26)return hash_copy(h);
    if(id==27){if(h->count){memset(h->pairs,0,h->count*sizeof(Pair));h->count=0;hash_drop_index(h);hash_changed(h);}return args[0];}
    /* id 28: Merge(destination, source) writes every source pair into destination. */
    if(args[1].tag!=HASH){failure(f,"Hash.Merge requires a hash source.");return empty;}
    Hash*source=pointer(args[1].ref);u32 count=source->count;
    for(u32 i=0;i<count;i++){
        Pair p=source->pairs[i];u32 j=hash_find(h,p.key);
        if(j==h->count)aqua_hash_resize(address(h),h->count+1);
        h->pairs[j]=p;
    }
    if(count)hash_changed(h);
    return args[0];
}
