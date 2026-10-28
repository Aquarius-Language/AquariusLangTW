/* Builtin IDs 6..19: portable array library. Included after runtime value/allocation helpers. */
#define ARRAY_LIMIT 33554431u
static Text *utf16(const uint16_t *s) {
    u32 n=0;while(s[n])n++;Text*t=pointer(aqua_text(n));memcpy(t->data,s,n*2);return t;
}
static int text_is(Text*t,const uint16_t*s) {
    u32 i=0;while(i<t->length&&s[i]&&t->data[i]==s[i])i++;return i==t->length&&!s[i];
}
/* The Array library: ids, bilingual names and arities live only here; the host never implements or lists them. */
#define ARRAY_FIRST_ID 6u
#define ARRAY_COUNT 14u
static const uint16_t *const array_module_names[2]={u"Array",u"陣列"};
static const uint16_t *const array_names[ARRAY_COUNT][2]={{u"Make",u"建立"},{u"Len",u"長度"},{u"Cap",u"容量"},{u"Grow",u"擴充"},{u"Append",u"追加"},{u"AppendArray",u"追加陣列"},{u"Copy",u"複製"},{u"Clone",u"克隆"},{u"Slice",u"切片"},{u"Fill",u"填入"},{u"Delete",u"刪除"},{u"Reverse",u"反轉"},{u"Index",u"尋找"},{u"Contains",u"包含"}};
static const unsigned char array_minimum[ARRAY_COUNT]={2,1,1,2,2,2,2,1,3,2,3,1,2,2};
static const unsigned char array_maximum[ARRAY_COUNT]={3,1,1,2,2,2,5,1,3,2,3,1,2,2};
#define KERNEL_FIRST_ID 50u
#define KERNEL_COUNT 7u
static const uint16_t *const kernel_names[KERNEL_COUNT][2];
static Scope *array_library(void) {
    Scope*s=scope(0);s->captured=1;for(u32 i=0;i<ARRAY_COUNT;i++)for(u32 j=0;j<2;j++)declare(s,utf16(array_names[i][j]),value(BUILTIN,ARRAY_FIRST_ID+i,0));
    for(u32 i=0;i<KERNEL_COUNT;i++)for(u32 j=0;j<2;j++)declare(s,utf16(kernel_names[i][j]),value(BUILTIN,KERNEL_FIRST_ID+i,0));return s;
}
static void array_changed(Array*a) {
    if(a->exposed&&!a->dirty){a->dirty=1;a->dirty_next=pending.dirty_arrays;pending.dirty_arrays=a;}
}
static int array_size(Frame*f,Value v,u32*out) {
    if(v.tag!=INT||v.number<0||v.number>ARRAY_LIMIT){failure(f,"Array size/index must be a nonnegative integer within the Wasm array limit.");return 0;}
    *out=(u32)v.number;return 1;
}
static Value array_call(Frame*f,u32 id,Value*args,u32 n) {
    if(id<ARRAY_FIRST_ID||id>=ARRAY_FIRST_ID+ARRAY_COUNT||n<array_minimum[id-ARRAY_FIRST_ID]||n>array_maximum[id-ARRAY_FIRST_ID]){failure(f,"Incorrect Array argument count.");return empty;}
    u32 count=0,start=0,end=0;
    if(id==6){
        u32 capacity;if(!array_size(f,args[0],&count))return empty;capacity=count;
        if(n==3&&!array_size(f,args[2],&capacity))return empty;
        if(capacity<count){failure(f,"Array capacity must be at least its length.");return empty;}
        Array*a=array(capacity);a->count=count;a->storage->used=count;
        for(u32 i=0;i<count;i++)a->values[i]=args[1];return value(ARRAY,address(a),0);
    }
    if(args[0].tag!=ARRAY){failure(f,"Array function requires an array.");return empty;}
    Array*a=pointer(args[0].ref);
    if(id==7)return number(a->count,INT);
    if(id==8)return number(a->capacity,INT);
    if(id==9){
        if(!array_size(f,args[1],&count))return empty;
        if(count>ARRAY_LIMIT-a->count){failure(f,"Array capacity exceeds the Wasm array limit.");return empty;}
        if(a->count+count>a->capacity)writable_array(a,a->count+count);return args[0];
    }
    if(id==10||id==11){
        Array*b=0;count=1;
        if(id==11){if(args[1].tag!=ARRAY){failure(f,"Array.AppendArray requires an array source.");return empty;}b=pointer(args[1].ref);count=b->count;
            if(a->element)for(u32 i=0;i<count;i++)if(!element_fits(f,a,b->values[i]))return empty;}
        else if(!element_fits(f,a,args[1]))return empty;
        if(count>ARRAY_LIMIT-a->count){failure(f,"Array length exceeds the Wasm array limit.");return empty;}
        if(!count)return args[0];start=a->count;writable_array(a,start+count);
        if(b)memmove(a->values+start,b->values,count*sizeof(Value));else a->values[start]=args[1];
        a->count=start+count;a->storage->used=a->count;array_changed(a);return args[0];
    }
    if(id==12){
        if(args[1].tag!=ARRAY){failure(f,"Array.Copy requires an array source.");return empty;}
        Array*b=pointer(args[1].ref);
        if(n>=3&&!array_size(f,args[2],&start))return empty;
        if(n>=4&&!array_size(f,args[3],&end))return empty;
        if(start>a->count||end>b->count){failure(f,"Array.Copy index is out of bounds.");return empty;}
        count=a->count-start;if(count>b->count-end)count=b->count-end;
        if(a->element)for(u32 i=0;i<count&&i<b->count-end;i++)if(!element_fits(f,a,b->values[end+i]))return empty;
        if(n==5){u32 requested;if(!array_size(f,args[4],&requested))return empty;if(requested<count)count=requested;}
        if(count){writable_array(a,a->count);memmove(a->values+start,b->values+end,count*sizeof(Value));array_changed(a);}return number(count,INT);
    }
    if(id==13||id==14){
        end=a->count;
        if(id==14&&(!array_size(f,args[1],&start)||!array_size(f,args[2],&end)))return empty;
        if(start>end||end>a->count){failure(f,"Array slice range is out of bounds.");return empty;}
        Array*b=array(end-start);memcpy(b->values,a->values+start,b->count*sizeof(Value));return value(ARRAY,address(b),0);
    }
    if(id==15){
        if(!element_fits(f,a,args[1]))return empty;
        if(a->count){writable_array(a,a->count);for(u32 i=0;i<a->count;i++)a->values[i]=args[1];array_changed(a);}return args[0];
    }
    if(id==16){
        if(!array_size(f,args[1],&start)||!array_size(f,args[2],&end))return empty;
        if(start>end||end>a->count){failure(f,"Array delete range is out of bounds.");return empty;}
        if(start!=end){writable_array(a,a->count);memmove(a->values+start,a->values+end,(a->count-end)*sizeof(Value));
            count=a->count-(end-start);memset(a->values+count,0,(a->count-count)*sizeof(Value));
            a->count=count;a->storage->used=count;array_changed(a);}return args[0];
    }
    if(id==17){
        if(a->count>1){writable_array(a,a->count);for(u32 i=0,j=a->count-1;i<j;i++,j--){Value v=a->values[i];a->values[i]=a->values[j];a->values[j]=v;}array_changed(a);}return args[0];
    }
    for(u32 i=0;i<a->count;i++)if(equal(a->values[i],args[1],0))return id==18?number(i,INT):value(BOOL,0,1);
    return id==18?number(-1,INT):value(BOOL,0,0);
}
/* Builtin IDs 50..56: array kernels of the same module. Sort, Sum, Min and Max run here. Map, Filter, Reduce and Sort
   with a comparison function call back into Aquarius, so after checking their arguments they run functions of the guest
   prelude (runtime/guest/prelude.aqua), which the compiler adds to programs that name them (Function.flags bits 8..15):
   their loops are ordinary resumable Aquarius code, so fuel, yields and errors behave as in user code. */
static const uint16_t *const kernel_names[KERNEL_COUNT][2]={{u"Sort",u"排序"},{u"Sum",u"總和"},{u"Min",u"最小值"},{u"Max",u"最大值"},{u"Map",u"映射"},{u"Filter",u"篩選"},{u"Reduce",u"歸約"}};
static const unsigned char kernel_minimum[KERNEL_COUNT]={1,1,1,1,2,2,3};
static const unsigned char kernel_maximum[KERNEL_COUNT]={2,1,1,1,2,2,3};
static void int_sift(int32_t*x,u32 root,u32 n) {
    for(;;){u32 c=root*2+1;if(c>=n)return;if(c+1<n&&x[c+1]>x[c])c++;if(x[root]>=x[c])return;int32_t t=x[root];x[root]=x[c];x[c]=t;root=c;}
}
/* Introsort: median-of-three Hoare partitions, heapsort past 2·log2(n) levels, insertion sort below 17 elements. */
static void int_introsort(int32_t*x,u32 n,u32 depth) {
    while(n>16){
        if(!depth){for(u32 i=n/2;i-->0;)int_sift(x,i,n);for(u32 i=n;i-->1;){int32_t t=x[0];x[0]=x[i];x[i]=t;int_sift(x,0,i);}return;}
        depth--;int32_t a=x[0],b=x[n/2],c=x[n-1],p=a<b?(b<c?b:a<c?c:a):(a<c?a:b<c?c:b);
        int32_t i=-1,j=(int32_t)n;
        for(;;){do i++;while(x[i]<p);do j--;while(x[j]>p);if(i>=j)break;int32_t t=x[i];x[i]=x[j];x[j]=t;}
        u32 left=(u32)j+1,right=n-left;
        if(left<right){int_introsort(x,left,depth);x+=left;n=right;}else{int_introsort(x+left,right,depth);n=left;}
    }
    for(u32 i=1;i<n;i++){int32_t v=x[i];u32 j=i;while(j&&x[j-1]>v){x[j]=x[j-1];j--;}x[j]=v;}
}
/* Orders of the stable sort: numbers by value with NaN last, texts by UTF-16 code units. */
static int number_before(Value a,Value b) { return a.number<b.number||(b.number!=b.number&&a.number==a.number); }
static int text_before(Value a,Value b) {
    Text*x=pointer(a.ref),*y=pointer(b.ref);u32 n=x->length<y->length?x->length:y->length;
    for(u32 i=0;i<n;i++)if(x->data[i]!=y->data[i])return x->data[i]<y->data[i];return x->length<y->length;
}
/* Stable bottom-up merge sort (texts or numbers): insertion-sorted runs of 16, then merges between v and t. The order is
   chosen by a flag, not a function pointer: the guest runtime shares the application's function table. */
static int before(Value a,Value b,int texts) { return texts?text_before(a,b):number_before(a,b); }
static void merge_sort(Value*v,Value*t,u32 n,int texts) {
    for(u32 lo=0;lo<n;lo+=16){u32 hi=lo+16<n?lo+16:n;for(u32 i=lo+1;i<hi;i++){Value x=v[i];u32 j=i;while(j>lo&&before(x,v[j-1],texts)){v[j]=v[j-1];j--;}v[j]=x;}}
    Value*from=v,*to=t;
    for(u32 w=16;w<n;w*=2){
        for(u32 lo=0;lo<n;lo+=2*w){u32 mid=lo+w<n?lo+w:n,hi=lo+2*w<n?lo+2*w:n,i=lo,j=mid,k=lo;
            while(i<mid&&j<hi)to[k++]=before(from[j],from[i],texts)?from[j++]:from[i++];
            while(i<mid)to[k++]=from[i++];while(j<hi)to[k++]=from[j++];}
        Value*s=from;from=to;to=s;
    }
    if(from!=v)memcpy(v,from,n*sizeof(Value));
}
/* Which kinds of elements an array holds: 1 all integers, 2 all numbers, 4 all texts (an empty array has every kind). */
static u32 element_kinds(Array*a) {
    u32 ints=1,numbers=1,texts=1;
    for(u32 i=0;i<a->count&&(numbers|texts);i++){u32 t=a->values[i].tag;ints&=t==INT;numbers&=t>=INT&&t<=DOUBLE;texts&=t==STRING;}
    return ints|numbers<<1|texts<<2;
}
static Value array_sort(Frame*f,Array*a,Value self) {
    u32 n=a->count,kinds=element_kinds(a);
    if(!(kinds&6)){failure(f,"Array.Sort requires numbers or strings, or a comparison function / 排序需要數字或字串，或比較函式。");return empty;}
    if(n<2)return self;
    writable_array(a,n);
    if(kinds&1){
        int32_t*x=allocate(n*4,RAW);u32 depth=0;for(u32 m=n;m;m>>=1)depth+=2;
        for(u32 i=0;i<n;i++)x[i]=(int32_t)a->values[i].number;
        int_introsort(x,n,depth);for(u32 i=0;i<n;i++)a->values[i]=value(INT,0,x[i]);release(x);
    }else{Value*t=allocate(n*sizeof(Value),RAW);merge_sort(a->values,t,n,!(kinds&2));release(t);}
    array_changed(a);return self;
}
/* Sum: left to right with the rules of +, starting from the first element (0 for an empty array). */
static Value array_sum(Frame*f,Array*a) {
    u32 kinds=element_kinds(a);
    if(!(kinds&2)){failure(f,"Array.Sum requires numbers / 總和需要數字。");return empty;}
    if(!a->count)return number(0,INT);
    if(kinds&1){u32 s=0;for(u32 i=0;i<a->count;i++)s+=(u32)(int32_t)a->values[i].number;return integer((int32_t)s);}
    Value s=a->values[0];for(u32 i=1;i<a->count;i++)s=binary(f,0,s,a->values[i],0);return s;
}
/* Min and Max: the first smallest or largest element (the first NaN, if a number is NaN); null for an empty array. */
static Value array_extreme(Frame*f,Array*a,int largest) {
    u32 kinds=element_kinds(a);
    if(!(kinds&6)){failure(f,largest?"Array.Max requires numbers or strings / 最大值需要數字或字串。":"Array.Min requires numbers or strings / 最小值需要數字或字串。");return empty;}
    if(!a->count)return value(NULL_VALUE,0,0);
    Value best=a->values[0];
    for(u32 i=1;i<a->count;i++){Value v=a->values[i];
        if(kinds&2){if(best.number!=best.number)break;if(v.number!=v.number){best=v;break;}if(largest?v.number>best.number:v.number<best.number)best=v;}
        else if(largest?text_before(best,v):text_before(v,best))best=v;}
    return best;
}
static int callable(Value v) { return v.tag==CLOSURE||v.tag==BUILTIN||v.tag==NATIVE||v.tag==STRUCT_TYPE; }
/* Runs a kernel into *out, or returns the prelude function (1 Sort with a comparison, 2 Map, 3 Filter, 4 Reduce) the
   call continues in. */
static u32 kernel_call(Frame*f,u32 id,Value*args,u32 n,Value*out) {
    *out=empty;
    if(n<kernel_minimum[id-KERNEL_FIRST_ID]||n>kernel_maximum[id-KERNEL_FIRST_ID]){failure(f,"Incorrect Array argument count.");return 0;}
    if(args[0].tag!=ARRAY){failure(f,"Array function requires an array.");return 0;}
    Array*a=pointer(args[0].ref);u32 k=id-KERNEL_FIRST_ID;
    if(k==0&&n==1){*out=array_sort(f,a,args[0]);return 0;}
    if(k==1){*out=array_sum(f,a);return 0;}
    if(k==2||k==3){*out=array_extreme(f,a,k==3);return 0;}
    if(!callable(args[1])){failure(f,"Array function requires a function / 陣列函式需要函式。");return 0;}
    return k==0?1:k-2;
}
/* The prelude function k as a closure of an empty root scope (Function.flags bits 8..15 name it). */
static Program *prelude_program;
static u32 prelude_ids[8];
static Closure *prelude_closure(Frame*f,u32 k) {
    Program*p=f->program;
    if(prelude_program!=p){memset(prelude_ids,0,sizeof prelude_ids);prelude_program=p;for(u32 i=0;i<p->count;i++){u32 x=p->functions[i].flags>>8&0xffu;if(x&&x<8)prelude_ids[x]=i+1;}}
    if(!prelude_ids[k]){failure(f,"This program was compiled without the array prelude / 程式缺少陣列前導函式。");return 0;}
    Scope*s=scope(0);s->captured=1;
    Closure*c=allocate(sizeof(Closure),CLOSURES);c->program=p;c->function=prelude_ids[k]-1;c->scope=s;c->context=f->context;return c;
}
