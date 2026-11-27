from PIL import Image, ImageDraw
C1=(128,97,246); C2=(69,125,246)
def grad(w,h):
    g=Image.new('RGB',(256,256)); px=g.load()
    for y in range(256):
        for x in range(256):
            t=(x+y)/510; px[x,y]=tuple(int(C1[i]+(C2[i]-C1[i])*t) for i in range(3))
    return g.resize((w,h),Image.BICUBIC)
def tiles(im,cx,cy,unit):
    S=4; W,H=im.size
    big=im.resize((W*S,H*S),Image.BICUBIC); d=ImageDraw.Draw(big)
    a=int(unit*S); gap=int(a*0.29); r=int(a*0.28)
    x0=int(cx*S-(2*a+gap)/2); y0=int(cy*S-(2*a+gap)/2)
    for i,(x,y) in enumerate([(x0,y0),(x0+a+gap,y0),(x0,y0+a+gap),(x0+a+gap,y0+a+gap)]):
        if i<3: d.rounded_rectangle((x,y,x+a,y+a),radius=r,fill='white')
        else: d.ellipse((x,y,x+a,y+a),fill='white')
    return big.resize((W,H),Image.LANCZOS)
# 1:1 : l'icône occupe toute la surface (même proportions que l'icône de l'appli)
n=2160; im=tiles(grad(n,n),n/2,n/2,n*0.205/0.94)
im.save('CentrePC-zone-1x1.png',optimize=True)
# 9:16 : même dessin, fond dégradé plein cadre
w,h=1440,2160; im=tiles(grad(w,h),w/2,h/2,w*0.205/0.94)
im.save('CentrePC-affiche-9x16.png',optimize=True)
for f in ['CentrePC-zone-1x1.png','CentrePC-affiche-9x16.png']:
    p=Image.open(f); p.thumbnail((400,400)); p.save('prev-'+f)
