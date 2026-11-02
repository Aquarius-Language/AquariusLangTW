/* Exact conversions between binary floating point and decimal text, with small fixed-size bignums: the shortest digits
 * that read back to a double or float and the language's number text (印出, String.From), and the double nearest to a
 * decimal number (String.ParseNumber).
 * Freestanding C (only <stdint.h>), so the guest runtime and the host's 印出 (runtime/host/format.c) share it and print
 * every number identically on every target. */
#ifndef AQUA_DECIMAL_H
#define AQUA_DECIMAL_H
#include <stdint.h>

static double decimal_bits_double(uint64_t bits) { union { uint64_t i; double d; } u;u.i=bits;return u.d; }
static uint64_t decimal_double_bits(double d) { union { double d; uint64_t i; } u;u.d=d;return u.i; }
/* Little-endian base-2^32 naturals of up to 5120 bits: enough for 780 decimal digits times 2^1075 or 10^1100. */
#define BIG_WORDS 160u
typedef struct { uint32_t n; uint32_t w[BIG_WORDS]; } Big;
static void big_set(Big*a,uint64_t v) { a->n=0;while(v){a->w[a->n++]=(uint32_t)v;v>>=32;} }
static void big_mul_small(Big*a,uint32_t m) {
    uint64_t carry=0;for(uint32_t i=0;i<a->n;i++){uint64_t t=(uint64_t)a->w[i]*m+carry;a->w[i]=(uint32_t)t;carry=t>>32;}
    if(carry)a->w[a->n++]=(uint32_t)carry;
}
static void big_add_small(Big*a,uint32_t v) {
    uint64_t carry=v;for(uint32_t i=0;i<a->n&&carry;i++){uint64_t t=(uint64_t)a->w[i]+carry;a->w[i]=(uint32_t)t;carry=t>>32;}
    if(carry)a->w[a->n++]=(uint32_t)carry;
}
static void big_shift(Big*a,uint32_t bits) {
    if(!a->n)return;uint32_t words=bits/32,rest=bits%32;
    if(rest){uint32_t carry=0;for(uint32_t i=0;i<a->n;i++){uint32_t v=a->w[i];a->w[i]=(v<<rest)|carry;carry=v>>(32-rest);}if(carry)a->w[a->n++]=carry;}
    if(words){for(int i=(int)a->n-1;i>=0;i--)a->w[i+words]=a->w[i];for(uint32_t i=0;i<words;i++)a->w[i]=0;a->n+=words;}
}
static int big_compare(const Big*a,const Big*b) {
    if(a->n!=b->n)return a->n<b->n?-1:1;
    for(int i=(int)a->n-1;i>=0;i--)if(a->w[i]!=b->w[i])return a->w[i]<b->w[i]?-1:1;return 0;
}
static void big_add(Big*out,const Big*a,const Big*b) {
    const Big*x=a->n>=b->n?a:b,*y=a->n>=b->n?b:a;uint64_t carry=0;uint32_t n=x->n;
    for(uint32_t i=0;i<n;i++){uint64_t t=(uint64_t)x->w[i]+(i<y->n?y->w[i]:0)+carry;out->w[i]=(uint32_t)t;carry=t>>32;}
    out->n=n;if(carry)out->w[out->n++]=(uint32_t)carry;
}
static void big_sub(Big*a,const Big*b) {   /* a -= b, requires a >= b */
    int64_t borrow=0;for(uint32_t i=0;i<a->n;i++){int64_t t=(int64_t)a->w[i]-(i<b->n?b->w[i]:0)-borrow;borrow=t<0;a->w[i]=(uint32_t)(t+(borrow?4294967296LL:0));}
    while(a->n&&!a->w[a->n-1])a->n--;
}
static void big_pow10(Big*a,uint32_t n) { for(;n>=9;n-=9)big_mul_small(a,1000000000u);static const uint32_t small[9]={1,10,100,1000,10000,100000,1000000,10000000,100000000};big_mul_small(a,small[n]); }

/* ---- Exact shortest round-trip digits (Burger & Dybvig free-format algorithm with bignums) ----
 * Produces the shortest digit string that reads back as the same value and, among
 * those, the one closest to it — the digits ECMAScript Number#toString prints. */
/* value = f * 2^e; lower_closer marks exact powers of two above the smallest normal.
 * Exact ties between two shortest candidates go to the even digit (Number#toString, and the
 * hosts' earlier printf-based 印出 for floats and doubles alike). */
static int shortest_digits(uint64_t f,int e,int lower_closer,char*digits,int*k) {
    Big r,s,mplus,mminus;int even=!(f&1);
    if(e>=0){
        big_set(&r,f);big_shift(&r,(uint32_t)e+(lower_closer?2:1));big_set(&s,lower_closer?4:2);
        big_set(&mplus,1);big_shift(&mplus,(uint32_t)e+(lower_closer?1:0));big_set(&mminus,1);big_shift(&mminus,(uint32_t)e);
    } else {
        big_set(&r,f);big_shift(&r,lower_closer?2:1);big_set(&s,1);big_shift(&s,(uint32_t)(-e)+(lower_closer?2:1));
        big_set(&mplus,lower_closer?2:1);big_set(&mminus,1);
    }
    int bits=0;for(uint64_t t=f;t;t>>=1)bits++;
    int estimate=0;{double guess=(e+bits-1)*0.30102999566398114-1e-10;estimate=(int)guess;if(guess>estimate)estimate++;}
    if(estimate>=0)for(int i=0;i<estimate;i++)big_mul_small(&s,10);
    else for(int i=0;i<-estimate;i++){big_mul_small(&r,10);big_mul_small(&mplus,10);big_mul_small(&mminus,10);}
    Big high;big_add(&high,&r,&mplus);
    if(big_compare(&high,&s)>=(even?0:1))estimate++;
    else{big_mul_small(&r,10);big_mul_small(&mplus,10);big_mul_small(&mminus,10);}
    *k=estimate;int length=0;
    for(;;){
        int digit=0;while(big_compare(&r,&s)>=0){big_sub(&r,&s);digit++;}
        int low=even?big_compare(&r,&mminus)<=0:big_compare(&r,&mminus)<0;
        big_add(&high,&r,&mplus);int up=even?big_compare(&high,&s)>=0:big_compare(&high,&s)>0;
        if(!low&&!up){digits[length++]=(char)('0'+digit);big_mul_small(&r,10);big_mul_small(&mplus,10);big_mul_small(&mminus,10);continue;}
        if(low&&up){Big twice=r;big_shift(&twice,1);int cmp=big_compare(&twice,&s);if(cmp>0||(cmp==0&&(digit&1)))digit++;}
        else if(up)digit++;
        digits[length++]=(char)('0'+digit);return length;
    }
}

/* The display text of a float (single) or double, as 印出 and String.From show it on every target: the shortest digits
 * that read back to the same value; scientific notation (1E+17, 1E-05: at least two exponent digits) from 10^17 for
 * doubles and 10^9 for floats, and below 10^-4 (the language's earlier .NET host); NaN, Infinity, -Infinity, -0.
 * Writes at most 32 ASCII characters to out and returns the length. */
static int format_number_text(char*out,double x,int single) {
    int n=0;
    if(x!=x){out[0]='N';out[1]='a';out[2]='N';return 3;}
    uint64_t bits=decimal_double_bits(x);
    if(bits>>63){out[n++]='-';x=-x;}
    if(x==0){out[n++]='0';return n;}
    if(x>1.7976931348623157e308){const char*inf="Infinity";while(*inf)out[n++]=*inf++;return n;}
    char digits[40];int length=0,point=0;
    if(single){
        union { float f; uint32_t i; } s;s.f=(float)x;
        uint32_t mantissa=s.i&0x7FFFFFu,biased=(s.i>>23)&0xFFu;
        if(biased)length=shortest_digits(mantissa|0x800000u,(int)biased-150,mantissa==0&&biased>1,digits,&point);
        else length=shortest_digits(mantissa,-149,0,digits,&point);
    } else {
        uint64_t mantissa=decimal_double_bits(x)&0xFFFFFFFFFFFFFull;uint32_t biased=(uint32_t)(decimal_double_bits(x)>>52)&0x7FFu;
        if(biased)length=shortest_digits(mantissa|0x10000000000000ull,(int)biased-1075,mantissa==0&&biased>1,digits,&point);
        else length=shortest_digits(mantissa,-1074,0,digits,&point);
    }
    int exponent=point-1;
    if(exponent>=(single?9:17)||exponent<-4){
        out[n++]=digits[0];
        if(length>1){out[n++]='.';for(int i=1;i<length;i++)out[n++]=digits[i];}
        out[n++]='E';out[n++]=exponent<0?'-':'+';if(exponent<0)exponent=-exponent;
        if(exponent>=100)out[n++]=(char)('0'+exponent/100);
        out[n++]=(char)('0'+exponent/10%10);out[n++]=(char)('0'+exponent%10);
        return n;
    }
    if(exponent<0){out[n++]='0';out[n++]='.';for(int i=-1;i>exponent;i--)out[n++]='0';for(int i=0;i<length;i++)out[n++]=digits[i];return n;}
    for(int i=0;i<length||i<=exponent;i++){if(i==exponent+1)out[n++]='.';out[n++]=i<length?digits[i]:'0';}
    return n;
}

/* A 64-bit integer's decimal text (長整數 / long: plain digits, never exponent notation) into out, at most 20 characters;
   returns the length. */
static int format_long_text(char*out,int64_t n) {
    uint64_t u=n<0?0-(uint64_t)n:(uint64_t)n;char rev[20];int r=0,k=0;
    do{rev[r++]=(char)('0'+u%10);u/=10;}while(u);
    if(n<0)out[k++]='-';while(r)out[k++]=rev[--r];return k;
}

/* ---- Decimal to double, correctly rounded (to nearest, ties to even) ----
 * value = D × 10^exponent, D the count decimal digits (no leading zeros). A first guess from the leading 19 digits is
 * corrected by comparing the exact value with the midpoints next to the guess, so the result is exact for any input. */
#define DECIMAL_DIGITS 780
/* Sign of D × 10^exponent - (2m + 1 - 2·below) × 2^k, for m and k of a candidate midpoint. */
static int decimal_compare(const char*digits,int count,int exponent,uint64_t odd,int k) {
    Big lhs,rhs;big_set(&lhs,0);
    for(int i=0;i<count;i++){big_mul_small(&lhs,10);big_add_small(&lhs,(uint32_t)(digits[i]-'0'));}
    big_set(&rhs,odd);
    if(exponent>=0)big_pow10(&lhs,(uint32_t)exponent);else big_pow10(&rhs,(uint32_t)-exponent);
    if(k>=0)big_shift(&rhs,(uint32_t)k);else big_shift(&lhs,(uint32_t)-k);
    return big_compare(&lhs,&rhs);
}
static double decimal_to_double(const char*digits,int count,int exponent) {
    if(!count)return 0;
    if(count+exponent>310)return __builtin_inf();
    if(count+exponent<-324)return 0;
    /* The guess: the leading digits scaled by powers of ten (within a few units in the last place). */
    int used=count<19?count:19;uint64_t m=0;for(int i=0;i<used;i++)m=m*10+(uint64_t)(digits[i]-'0');
    int scale=exponent+(count-used);double z=(double)m;
    for(;scale>=22;scale-=22)z*=1e22;for(;scale<=-22;scale+=22)z/=1e22;
    static const double powers[22]={1,1e1,1e2,1e3,1e4,1e5,1e6,1e7,1e8,1e9,1e10,1e11,1e12,1e13,1e14,1e15,1e16,1e17,1e18,1e19,1e20,1e21};
    z=scale>=0?z*powers[scale]:z/powers[-scale];
    uint64_t bits=decimal_double_bits(z);
    if(bits>=0x7ff0000000000000ull)bits=0x7fefffffffffffffull;
    if(!bits)bits=1;
    for(;;){
        /* bits encodes m × 2^e: biased exponent b, mantissa with the hidden bit when normal. */
        uint32_t b=(uint32_t)(bits>>52);uint64_t mantissa=bits&0xfffffffffffffull;int e;
        if(b){mantissa|=1ull<<52;e=(int)b-1075;}else e=-1074;
        /* Above: (2m + 1) × 2^(e - 1). */
        int above=decimal_compare(digits,count,exponent,2*mantissa+1,e-1);
        if(above>0||(above==0&&(mantissa&1))){
            if(bits==0x7fefffffffffffffull)return above>0||mantissa&1?__builtin_inf():decimal_bits_double(bits);
            if(above==0)return decimal_bits_double(bits+1);
            bits++;continue;
        }
        /* Below: (2m - 1) × 2^(e - 1), or (4m - 1) × 2^(e - 2) just above a power of two (the gap below is half as wide). */
        int narrow=b>1&&mantissa==1ull<<52;
        int below=narrow?decimal_compare(digits,count,exponent,4*mantissa-1,e-2):decimal_compare(digits,count,exponent,2*mantissa-1,e-1);
        if(below<0||(below==0&&(mantissa&1))){
            if(bits==1)return below<0||mantissa&1?0:decimal_bits_double(bits);
            if(below==0)return decimal_bits_double(bits-1);
            bits--;continue;
        }
        return decimal_bits_double(bits);
    }
}
#endif
